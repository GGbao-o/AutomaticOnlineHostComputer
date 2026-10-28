using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>使用真实候选/轮询方法验证门条件。FANUC仅注入内存快照，不启动Worker或调用原生SDK。</summary>
public sealed class SkewDoorCandidateContractTests
{
    [Theory]
    [InlineData(typeof(Line1RearFlowEngine))]
    [InlineData(typeof(Line2RearFlowEngine))]
    public void Closed_bed_is_skipped_but_another_open_bed_remains_available(Type type)
    {
        using var fixture = new RearFixture(type);
        object closed = fixture.Bed;
        fixture.Set(closed, "DoorOpen", false);
        object open = fixture.AddBed(1, "OPEN-BED");
        fixture.Set(open, "DoorOpen", true);
        var ready = (IList)fixture.Invoke("GetReadyBedsByPriority")!;
        Assert.Single(ready.Cast<object>());
        Assert.Same(open, ready[0]);
        Assert.False((bool)fixture.GetEngine("_paused")!);

        // 下一轮该床门已开，可以重新参与；原请求仍在，不必重新创建任务。
        fixture.Set(closed, "DoorOpen", true);
        Assert.Equal(2, ((IList)fixture.Invoke("GetReadyBedsByPriority")!).Count);
        fixture.Set(closed, "SignalFresh", false);
        Assert.Single(((IList)fixture.Invoke("GetReadyBedsByPriority")!).Cast<object>());
    }

    [Theory]
    [InlineData(typeof(Line1RearFlowEngine))]
    [InlineData(typeof(Line2RearFlowEngine))]
    public async Task Closed_door_preserves_workpiece_and_completion_export_while_blocking_unload(Type type)
    {
        using var fixture = new RearFixture(type);
        fixture.Set(fixture.Bed, "DoorOpen", false);
        fixture.Set(fixture.Bed, "RqUnload", true);
        fixture.SetState("Machining");
        var wp = new WorkpieceCache { PlateNo = "DOOR-TEST", Sequence = "1", Length = 900, Diameter = 100 };
        fixture.Set(fixture.Bed, "Wp", wp);

        // 写入测试专用临时文件，验证门关不阻止原job2导出，不触碰现场配置路径。
        string path = Path.Combine(Path.GetTempPath(), "skew-door-" + Guid.NewGuid().ToString("N") + ".txt");
        fixture.Config.SkewCompletionExport.Enabled = true;
        fixture.Config.SkewCompletionExport.FilePath = path;
        fixture.Config.SkewCompletionExport.StationMachineCodes[fixture.Code] = "TEST-MACHINE";
        try
        {
            for (int i = 0; i < 2; i++)
            {
                Task scanTask = (Task)fixture.Invoke("BuildUnloadCandidateScanAsync", null, false, CancellationToken.None)!;
                await scanTask;
                object result = scanTask.GetType().GetProperty("Result")!.GetValue(scanTask)!;
                var candidates = (IList)result.GetType().GetProperty("Candidates")!.GetValue(result)!;
                Assert.Empty(candidates.Cast<object>());
            }
            Assert.Equal("WaitingUnload", fixture.State);
            Assert.Equal(wp, fixture.Get(fixture.Bed, "Wp"));
            Assert.True((bool)fixture.Get(fixture.Bed, "CompletionExported")!);
            Assert.Single(File.ReadAllLines(path));
            Assert.Contains("DOOR-TEST,1,TEST-MACHINE,", File.ReadAllText(path));
            Assert.False((bool)fixture.GetEngine("_paused")!);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(typeof(Line1RearFlowEngine), "ST112")]
    [InlineData(typeof(Line2RearFlowEngine), "ST606")]
    [InlineData(typeof(Line2RearFlowEngine), "ST607")]
    [InlineData(typeof(Line2RearFlowEngine), "ST608")]
    [InlineData(typeof(Line2RearFlowEngine), "ST609")]
    [InlineData(typeof(Line2RearFlowEngine), "ST610")]
    public async Task Fanuc_poll_consumes_door_snapshot_and_does_not_reuse_true_after_read_failure(Type type, string code)
    {
        using var fixture = new RearFixture(type, code);
        var snapshot = new FanucSignalSnapshot { Connected = true, RqData = true, Opened = false };
        typeof(FanucSignalSnapshot).GetProperty("DoorOpen")!.SetValue(snapshot, true);
        fixture.Publish(snapshot);
        await fixture.Poll();
        Assert.True((bool)fixture.Get(fixture.Bed, "DoorOpen")!);
        Assert.True((bool)fixture.Get(fixture.Bed, "SignalFresh")!);
        Assert.Single(((IList)fixture.Invoke("GetReadyBedsByPriority")!).Cast<object>());

        fixture.Publish(new FanucSignalSnapshot { Connected = false, Error = "测试读取失败" });
        await fixture.Poll();
        Assert.False((bool)fixture.Get(fixture.Bed, "DoorOpen")!);
        Assert.False((bool)fixture.Get(fixture.Bed, "SignalFresh")!);
        Assert.Empty(((IList)fixture.Invoke("GetReadyBedsByPriority")!).Cast<object>());
    }

    [Theory]
    [InlineData("Line1RearFlowEngine.cs")]
    [InlineData("Line2RearFlowEngine.cs")]
    public void Locked_rechecks_keep_the_shared_candidate_gates_and_export_precedes_door_filter(string file)
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", file));
        Assert.Contains("b.RqData && b.DoorOpen", source);
        int unload = source.IndexOf("private async Task<UnloadCandidateScan> BuildUnloadCandidateScanAsync", StringComparison.Ordinal);
        int export = source.IndexOf("SkewCompletionExportHelper.TryAppendAsync", unload, StringComparison.Ordinal);
        int door = source.IndexOf("if (!b.DoorOpen)", unload, StringComparison.Ordinal);
        int destination = source.IndexOf("CanUnloadDestinationAcceptAsync", unload, StringComparison.Ordinal);
        Assert.True(export > unload && door > export && destination > door);
        string rejected = source[door..destination];
        Assert.Contains("continue;", rejected);
        foreach (string sideEffect in new[] { "_paused =", "Wp =", "ClearSkew", "MoveAbsoluteAsync", "AcquireAsync" })
            Assert.DoesNotContain(sideEffect, rejected);
        foreach ((string method, string scan) in new[]
                 {
                     ("TryDispatchLoadAsync", "BuildLoadCandidateScan(logDetails: true)"),
                     ("TryDispatchUnloadAsync", "BuildUnloadCandidateScanAsync(fds, logDetails: true, ct)")
                 })
        {
            int start = source.IndexOf($"private async Task {method}", StringComparison.Ordinal);
            int locked = source.IndexOf("_craneRearLock.WaitAsync(0, ct)", start, StringComparison.Ordinal);
            int rescan = source.IndexOf(scan, start, StringComparison.Ordinal);
            Assert.True(locked > start && rescan > locked);
        }
    }

    private sealed class RearFixture : IDisposable
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly Type _type;
        private readonly Type _bedType;
        private readonly object _engine;
        private readonly FanucSkewBedService _fanuc = new("127.0.0.1");
        private readonly object _sdk;
        private readonly Array _beds;
        internal MotionConfig Config { get; } = new();
        internal object Bed { get; }
        internal string Code { get; }
        internal string State => _bedType.GetProperty("St")!.GetValue(Bed)!.ToString()!;

        internal RearFixture(Type type, string? code = null)
        {
            _type = type;
            _bedType = type.GetNestedType("SkewCtx", BindingFlags.NonPublic)!;
            _engine = RuntimeHelpers.GetUninitializedObject(type);
            _beds = Array.CreateInstance(_bedType, 5);
            type.GetField("_beds", Fields)!.SetValue(_engine, _beds);
            type.GetField("_cfg", Fields)!.SetValue(_engine, Config);
            type.GetField("_safety", Fields)!.SetValue(_engine, new SafetyFlags());
            // 只模拟托管连接状态；绝不启动Worker，不调用Connect/GetMacro/Dispose中的原生句柄释放。
            _sdk = typeof(FanucSkewBedService).GetField("_sdk", Fields)!.GetValue(_fanuc)!;
            _sdk.GetType().GetField("_connected", Fields)!.SetValue(_sdk, true);
            Code = code ?? (type == typeof(Line1RearFlowEngine) ? "ST112" : "ST606");
            Bed = AddBed(0, Code);
            Config.SkewCompletionExport.Enabled = false;
        }

        internal object AddBed(int index, string code)
        {
            object bed = Activator.CreateInstance(_bedType, true)!;
            Set(bed, "Code", code); Set(bed, "F", _fanuc);
            Set(bed, "SignalFresh", true); Set(bed, "RqData", true);
            _beds.SetValue(bed, index);
            return bed;
        }

        internal void SetState(string name)
        {
            var state = _bedType.GetProperty("St")!;
            state.SetValue(Bed, Enum.Parse(state.PropertyType, name));
        }
        internal void Set(object target, string field, object? value)
            => (target.GetType().GetField(field, Fields) ?? throw new InvalidOperationException("Missing field: " + field)).SetValue(target, value);
        internal object? Get(object target, string field) => target.GetType().GetField(field, Fields)!.GetValue(target);
        internal object? GetEngine(string field) => Get(_engine, field);
        internal object? Invoke(string method, params object?[] args) => _type.GetMethod(method, Fields)!.Invoke(_engine, args);
        internal void Publish(FanucSignalSnapshot snapshot) => Set(_fanuc, "_latestSnapshot", snapshot);
        internal Task Poll()
        {
            string name = _type == typeof(Line1RearFlowEngine) ? "Line1RearDeviceStatus" : "Line2RearDeviceStatus";
            object ds = Activator.CreateInstance(_type.GetNestedType(name)!)!;
            return (Task)Invoke("PollBed", Bed, CancellationToken.None, ds, 0, 0L)!;
        }
        public void Dispose()
        {
            _sdk.GetType().GetField("_connected", Fields)!.SetValue(_sdk, false);
            _fanuc.Dispose();
        }
    }
}
