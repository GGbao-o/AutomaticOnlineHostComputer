using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class PausedWorkpieceDiscardTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [Theory]
    [InlineData(typeof(Line1FrontFlowEngine))]
    [InlineData(typeof(Line2FrontFlowEngine))]
    public void Manipulator_abort_discards_only_failed_identity_and_clears_current(Type type)
    {
        object engine = CreateEngine(type);
        WorkpieceCache failed = Board("A", "1");
        WorkpieceCache next = Board("A", "2");
        WorkpieceCache later = Board("B", "1");
        Seed(engine, failed, next, failed with { Length = 2000 }, later);
        Field(engine, "_currentWp").SetValue(engine, failed);

        Invoke(engine, "PauseAndDiscardManipulatorWorkpiece", failed);

        Assert.Null(Field(engine, "_currentWp").GetValue(engine));
        Assert.True((bool)Field(engine, "_paused").GetValue(engine)!);
        Assert.Equal(new[] { next, later }, Queue(engine));
        Assert.Equal(Queue(engine), Display(engine));
    }

    [Theory]
    [InlineData(typeof(Line1FrontFlowEngine))]
    [InlineData(typeof(Line2FrontFlowEngine))]
    public void Ordinary_manual_pause_keeps_current_and_source_queue(Type type)
    {
        object engine = CreateEngine(type);
        WorkpieceCache current = Board("A", "1");
        WorkpieceCache next = Board("A", "2");
        Field(engine, "_currentWp").SetValue(engine, current);
        Seed(engine, next);
        Field(engine, "<DeviceStatus>k__BackingField").SetValue(engine,
            Activator.CreateInstance(type.GetProperty("DeviceStatus")!.PropertyType));

        type.GetMethod("Pause")!.Invoke(engine, null);

        Assert.Equal(current, Field(engine, "_currentWp").GetValue(engine));
        Assert.Equal(new[] { next }, Queue(engine));
        Assert.Equal(Queue(engine), Display(engine));
    }

    [Fact]
    public void Grinding_soft_pause_preserves_source_queue()
    {
        object engine = CreateEngine(typeof(GrindingFlowEngine));
        WorkpieceCache head = Board("A", "1");
        WorkpieceCache next = Board("A", "2");
        Seed(engine, head, next);

        typeof(GrindingFlowEngine).GetMethod("PauseForCraneZeroPosition")!.Invoke(engine, null);

        Assert.Equal(new[] { head, next }, Queue(engine));
        Assert.Equal(Queue(engine), Display(engine));
    }

    [Theory]
    [InlineData(typeof(Line1FrontFlowEngine))]
    [InlineData(typeof(Line2FrontFlowEngine))]
    public void Old_manipulator_abort_does_not_clear_different_current_workpiece(Type type)
    {
        object engine = CreateEngine(type);
        WorkpieceCache next = Board("A", "2");
        Seed(engine, next);
        Field(engine, "_currentWp").SetValue(engine, next);

        Invoke(engine, "PauseAndDiscardManipulatorWorkpiece", Board("A", "1"));

        Assert.Equal(next, Field(engine, "_currentWp").GetValue(engine));
        Assert.Equal(new[] { next }, Queue(engine));
    }

    [Fact]
    public void Grinding_abort_removes_matching_residue_anywhere_without_deleting_next_head()
    {
        object engine = CreateEngine(typeof(GrindingFlowEngine));
        WorkpieceCache failed = Board("A", "1");
        WorkpieceCache next = Board("A", "2");
        WorkpieceCache later = Board("B", "1");
        Seed(engine, next, failed with { Length = 0 }, later, failed);

        Invoke(engine, "DiscardGrindingSourceWorkpiece", failed);

        Assert.Equal(new[] { next, later }, Queue(engine));
        Assert.Equal(Queue(engine), Display(engine));
        Invoke(engine, "DiscardGrindingSourceWorkpiece", failed);
        Assert.Equal(new[] { next, later }, Queue(engine));
    }

    [Theory]
    [InlineData(typeof(Line1FrontFlowEngine), "PauseAndDiscardManipulatorWorkpiece")]
    [InlineData(typeof(Line2FrontFlowEngine), "PauseAndDiscardManipulatorWorkpiece")]
    [InlineData(typeof(GrindingFlowEngine), "DiscardGrindingSourceWorkpiece")]
    public void Incomplete_identity_does_not_delete_other_anonymous_boards(Type type, string method)
    {
        object engine = CreateEngine(type);
        WorkpieceCache anonymous = Board("", "");
        Seed(engine, anonymous, Board("A", "2"));
        if (type != typeof(GrindingFlowEngine))
            Field(engine, "_currentWp").SetValue(engine, anonymous);

        Invoke(engine, method, anonymous);

        Assert.Equal(new[] { anonymous, Board("A", "2") }, Queue(engine));
        Assert.Equal(Queue(engine), Display(engine));
        if (type != typeof(GrindingFlowEngine))
            Assert.Null(Field(engine, "_currentWp").GetValue(engine));
    }

    [Theory]
    [InlineData(typeof(Line1FrontFlowEngine))]
    [InlineData(typeof(Line2FrontFlowEngine))]
    public void Concurrent_enqueue_and_abort_preserve_survivor_order_and_display(Type type)
    {
        for (int round = 0; round < 20; round++)
        {
            object engine = CreateEngine(type);
            WorkpieceCache failed = Board("A", "1");
            WorkpieceCache next = Board("A", "2");
            Seed(engine, failed, next);
            WorkpieceCache[] arrivals = Enumerable.Range(1, 5).Select(i => Board("C", i.ToString())).ToArray();

            Parallel.Invoke(
                () => Invoke(engine, "PauseAndDiscardManipulatorWorkpiece", failed),
                () =>
                {
                    foreach (WorkpieceCache wp in arrivals)
                        type.GetMethod("EnqueueWorkpiece", new[] { typeof(WorkpieceCache) })!.Invoke(engine, new object[] { wp });
                });

            Assert.Equal(new[] { next }.Concat(arrivals), Queue(engine));
            Assert.Equal(Queue(engine), Display(engine));
        }
    }

    [Theory]
    [InlineData("Line1FrontFlowEngine.cs")]
    [InlineData("Line2FrontFlowEngine.cs")]
    public void Manipulator_pausing_catches_discard_instead_of_requeue(string file)
    {
        string source = Read(file);
        string pickup = Slice(source, "private async Task ProcessManipulatorPickupAsync", "private async Task<bool> MoveManipulator1ToPickupStandbyAfterForkReleaseAsync");
        Assert.DoesNotContain("RequeueFrontCache(wp)", pickup);
        Assert.Equal(1, pickup.Split("PauseAndDiscardManipulatorWorkpiece(wp);").Length - 1);
        Assert.Contains("_manipulatorLock.Release();", pickup);
    }

    [Fact]
    public void Grinding_pausing_catches_do_not_requeue_but_nonpausing_paths_still_do()
    {
        string source = Read("GrindingFlowEngine.cs");
        string pickup = Slice(source, "private async Task ProcessWorkpieceAsync", "private async Task UnloadFromGrinderAsync");
        string pressure = Slice(pickup, "catch (PressureStopException pEx)", "catch (Exception ex)");
        Assert.DoesNotContain("RequeueWorkpiece(wp)", pressure);
        Assert.Contains("discardWorkpieceOnAbort = true;", pressure);
        Assert.Contains("RequeueWorkpiece(wp);", pickup); // TypeA准入退回保留；严重未充磁异常已禁止回队。
        string cleanup = pickup[pickup.LastIndexOf("\n        finally", StringComparison.Ordinal)..];
        Assert.Contains("discardWorkpieceOnAbort && IsGrindingActionCurrent(actionVersion)", cleanup);
        Assert.True(cleanup.IndexOf("DiscardGrindingSourceWorkpiece(wp)", StringComparison.Ordinal) <
                    cleanup.IndexOf("ClearGrinderOnAbortAsync", StringComparison.Ordinal));
        Assert.True(cleanup.IndexOf("ClearGrinderOnAbortAsync", StringComparison.Ordinal) <
                    cleanup.IndexOf("ReleaseRecordedCraneLock", StringComparison.Ordinal));
    }

    private static WorkpieceCache Board(string plate, string sequence) =>
        new() { PlateNo = plate, Sequence = sequence, Diameter = 100, Length = 1000 };

    private static object CreateEngine(Type type)
    {
        object engine = RuntimeHelpers.GetUninitializedObject(type);
        Field(engine, "_cacheLock").SetValue(engine, new object());
        Field(engine, "_cacheQueue").SetValue(engine, type == typeof(GrindingFlowEngine)
            ? new LinkedList<WorkpieceCache>() : new ConcurrentQueue<WorkpieceCache>());
        Field(engine, "_cachedList").SetValue(engine, new List<WorkpieceCache>());
        if (type != typeof(GrindingFlowEngine)) Field(engine, "_wpLock").SetValue(engine, new object());
        return engine;
    }

    private static void Seed(object engine, params WorkpieceCache[] boards)
    {
        foreach (WorkpieceCache wp in boards)
        {
            object queue = Field(engine, "_cacheQueue").GetValue(engine)!;
            if (queue is LinkedList<WorkpieceCache> linked) linked.AddLast(wp);
            else ((ConcurrentQueue<WorkpieceCache>)queue).Enqueue(wp);
            ((List<WorkpieceCache>)Field(engine, "_cachedList").GetValue(engine)!).Add(wp);
        }
    }

    private static WorkpieceCache[] Queue(object engine) =>
        ((IEnumerable<WorkpieceCache>)Field(engine, "_cacheQueue").GetValue(engine)!).ToArray();
    private static WorkpieceCache[] Display(object engine) =>
        ((List<WorkpieceCache>)Field(engine, "_cachedList").GetValue(engine)!).ToArray();
    private static FieldInfo Field(object engine, string name) => engine.GetType().GetField(name, PrivateInstance)!;
    private static void Invoke(object engine, string method, WorkpieceCache wp)
    {
        MethodInfo? target = engine.GetType().GetMethod(method, PrivateInstance);
        Assert.NotNull(target);
        target.Invoke(engine, new object[] { wp });
    }
    private static string Read(string file) => File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", file));
    private static string Slice(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, $"缺少起始方法/分支：{start}");
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"缺少结束方法/分支：{end}");
        return source[first..last];
    }
}
