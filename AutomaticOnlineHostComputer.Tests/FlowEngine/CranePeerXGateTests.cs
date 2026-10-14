using System.Text.Json;
using System.Xml.Linq;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Config;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>用可控读取/延时验证真实等待核心，不连接PLC、不移动设备。</summary>
public sealed class CranePeerXGateTests
{
    [Theory]
    [InlineData(1, 2, true, -14000, -14001)]
    [InlineData(2, 1, false, 2500, 2501)]
    [InlineData(3, 4, true, -14000, -14001)]
    [InlineData(4, 3, false, 2500, 2501)]
    public async Task Each_direction_waits_outside_the_boundary_then_accepts_equality(
        int current, int peer, bool minimum, int threshold, int outside)
    {
        int reads = 0, waits = 0;
        var logs = new List<string>();
        await CranePeerXGate.WaitAsync(current, peer,
            _ => Task.FromResult<int?>(++reads == 1 ? outside : threshold), () => threshold, minimum,
            (ms, _) => { Assert.Equal(500, ms); waits++; return Task.CompletedTask; },
            logs.Add, CancellationToken.None);
        Assert.Equal(2, reads);
        Assert.Equal(1, waits);
        Assert.Contains(logs, x => x.Contains($"{current}号读取{peer}号") && x.Contains("保持区域锁无限等待"));
        Assert.Contains(logs, x => x.Contains($"实际X={threshold}mm") && x.Contains("已满足"));
    }

    [Theory]
    [InlineData(true, -14000, 0)]
    [InlineData(false, 2500, -1000)]
    public async Task Already_past_the_boundary_continues_without_delay(bool minimum, int threshold, int actual)
    {
        await CranePeerXGate.WaitAsync(1, 2, _ => Task.FromResult<int?>(actual), () => threshold, minimum,
            (_, _) => throw new Exception("满足时不能延时"), _ => { }, CancellationToken.None);
    }

    [Fact]
    public async Task Position_wait_has_no_retry_limit_and_only_periodic_logs()
    {
        int reads = 0, waits = 0;
        var logs = new List<string>();
        await CranePeerXGate.WaitAsync(1, 2,
            _ => Task.FromResult<int?>(++reads <= 105 ? -15000 : -14000), () => -14000, true,
            (_, _) => { waits++; return Task.CompletedTask; }, logs.Add, CancellationToken.None);
        Assert.Equal(105, waits);
        Assert.Equal(106, reads);
        Assert.InRange(logs.Count, 2, 10); // 等待再久也不能每个500ms采样刷一行日志。
    }

    [Fact]
    public async Task Page_edit_changes_the_next_poll_of_an_existing_wait()
    {
        var config = new MotionConfig();
        var vm = new ConfigPageViewModel(config);
        int waits = 0;
        await CranePeerXGate.WaitAsync(1, 2, _ => Task.FromResult<int?>(-15000),
            () => config.CranePeerXAdmission.Crane1MinCrane2X, true,
            (_, _) => { waits++; vm.Crane1MinCrane2X = -15000; return Task.CompletedTask; },
            _ => { }, CancellationToken.None);
        Assert.Equal(1, waits);
        Assert.Equal(-15000, config.CranePeerXAdmission.Crane1MinCrane2X);
        Assert.Equal(-14000, config.CranePeerXAdmission.Crane3MinCrane4X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_position_throws_instead_of_using_zero_or_previous_sample(bool failAfterWait)
    {
        int reads = 0, waits = 0;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CranePeerXGate.WaitAsync(1, 2,
            _ => Task.FromResult<int?>(++reads == 1 && failAfterWait ? -15000 : null),
            () => -14000, true, (_, _) => { waits++; return Task.CompletedTask; },
            _ => { }, CancellationToken.None));
        Assert.Contains("2号天车实际X读取失败", ex.Message);
        Assert.Equal(failAfterWait ? 1 : 0, waits);
    }

    [Fact]
    public async Task Communication_exception_is_propagated_to_original_engine_cleanup()
    {
        var failure = new TimeoutException("PLC读取超时");
        var actual = await Assert.ThrowsAsync<TimeoutException>(() => CranePeerXGate.WaitAsync(2, 1,
            _ => throw failure, () => 2500, false,
            (_, _) => throw new Exception("通信失败不应正常等待重试"), _ => { }, CancellationToken.None));
        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task Cancellation_before_entry_reads_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CranePeerXGate.WaitAsync(1, 2,
            _ => throw new Exception("已取消不应读取"), () => -14000, true,
            (_, _) => Task.CompletedTask, _ => { }, cts.Token));
    }

    [Fact]
    public async Task Waiting_keeps_both_caller_locks_and_cancellation_leaves_release_to_caller()
    {
        using var mt = new SemaphoreSlim(1, 1);
        using var ts = new SemaphoreSlim(1, 1);
        using var cts = new CancellationTokenSource();
        await mt.WaitAsync();
        await ts.WaitAsync();
        try
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task gate = CranePeerXGate.WaitAsync(2, 1, _ => Task.FromResult<int?>(2501), () => 2500, false,
                async (_, ct) => { waiting.SetResult(); await Task.Delay(Timeout.Infinite, ct); },
                _ => { }, cts.Token);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(gate.IsCompleted);
            Assert.False(await mt.WaitAsync(0));
            Assert.False(await ts.WaitAsync(0));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate);
            Assert.Equal(0, mt.CurrentCount);
            Assert.Equal(0, ts.CurrentCount);
        }
        finally
        {
            // 生产亦由原finally释放；准入组件没有替调用方释放锁的权限。
            mt.Release();
            ts.Release();
        }
    }

    [Fact]
    public async Task Cancellation_during_read_cannot_pass_even_when_x_satisfies_condition()
    {
        using var cts = new CancellationTokenSource();
        bool passed = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CranePeerXGate.WaitAsync(1, 2,
            _ => { cts.Cancel(); return Task.FromResult<int?>(0); }, () => -14000, true,
            (_, _) => Task.CompletedTask, _ => passed = true, cts.Token));
        Assert.False(passed);
    }
}

/// <summary>验证旧JSON默认、四个独立有符号字段的保存格式、页面绑定及共享配置。</summary>
public sealed class CranePeerXConfigTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"cranePeerXAdmission\":{}}")]
    public void Old_json_uses_the_four_agreed_defaults(string json)
    {
        var config = JsonSerializer.Deserialize<MotionConfig>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(-14000, config.CranePeerXAdmission.Crane1MinCrane2X);
        Assert.Equal(2500, config.CranePeerXAdmission.Crane2MaxCrane1X);
        Assert.Equal(-14000, config.CranePeerXAdmission.Crane3MinCrane4X);
        Assert.Equal(2500, config.CranePeerXAdmission.Crane4MaxCrane3X);
    }

    [Fact]
    public void Page_properties_update_four_independent_values_and_round_trip_with_save_naming()
    {
        var config = new MotionConfig();
        var vm = new ConfigPageViewModel(config);
        vm.Crane1MinCrane2X = -15000;
        vm.Crane2MaxCrane1X = 0;
        vm.Crane3MinCrane4X = -16000;
        vm.Crane4MaxCrane3X = 3000;
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string json = JsonSerializer.Serialize(config, options);
        var loaded = JsonSerializer.Deserialize<MotionConfig>(json, options)!.CranePeerXAdmission;
        Assert.Contains("\"cranePeerXAdmission\":", json);
        Assert.Equal(-15000, loaded.Crane1MinCrane2X);
        Assert.Equal(0, loaded.Crane2MaxCrane1X);
        Assert.Equal(-16000, loaded.Crane3MinCrane4X);
        Assert.Equal(3000, loaded.Crane4MaxCrane3X);
    }

    [Fact]
    public void Shipped_json_and_other_tab_expose_all_four_thresholds()
    {
        string root = RepositoryRoot.Find();
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Config", "motion_settings.json")));
        var section = json.RootElement.GetProperty("cranePeerXAdmission");
        Assert.Equal(-14000, section.GetProperty("crane1MinCrane2X").GetInt32());
        Assert.Equal(2500, section.GetProperty("crane2MaxCrane1X").GetInt32());
        Assert.Equal(-14000, section.GetProperty("crane3MinCrane4X").GetInt32());
        Assert.Equal(2500, section.GetProperty("crane4MaxCrane3X").GetInt32());
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var view = XDocument.Load(Path.Combine(root, "Views", "Config", "ConfigPageView.xaml"));
        var tab = view.Descendants(ui + "TabItem").Single(x => (string?)x.Attribute("Header") == "其他");
        foreach (string field in new[] { "Crane1MinCrane2X", "Crane2MaxCrane1X", "Crane3MinCrane4X", "Crane4MaxCrane3X" })
            Assert.Single(tab.Descendants(ui + "TextBox").Where(x =>
                (string?)x.Attribute("Text") == $"{{Binding {field}, UpdateSourceTrigger=PropertyChanged}}"));
    }
}

/// <summary>核对四处入口的对车编号、比较方向和持锁范围，避免两条线复制时接错天车。</summary>
public sealed class CranePeerXGateEntryTests
{
    [Theory]
    [InlineData("Line1FrontFlowEngine.cs", 1, 2, "Crane1MinCrane2X", true)]
    [InlineData("Line2FrontFlowEngine.cs", 3, 4, "Crane3MinCrane4X", true)]
    [InlineData("Line1RearFlowEngine.cs", 2, 1, "Crane2MaxCrane1X", false)]
    [InlineData("Line2RearFlowEngine.cs", 4, 3, "Crane4MaxCrane3X", false)]
    public void Entry_checks_the_correct_peer_after_locking_and_before_original_work(
        string file, int current, int peer, string setting, bool atLeast)
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", file));
        int gate = source.IndexOf("await CranePeerXGate.WaitAsync(", StringComparison.Ordinal);
        Assert.True(gate >= 0, $"{file}缺少持锁X准入");
        string call = source[gate..source.IndexOf(';', gate)];
        Assert.Contains($"_craneCache.GetOrCreateService({peer}), {current}, {peer},", call);
        Assert.Contains($"() => _cfg.CranePeerXAdmission.{setting}", call);
        Assert.Contains($"requireAtLeast: {atLeast.ToString().ToLowerInvariant()}", call);

        if (atLeast)
        {
            int pause = source.LastIndexOf("while (_paused)", gate, StringComparison.Ordinal);
            int register = source.LastIndexOf("action.RegisterLock(\"ZoneMT\");", gate, StringComparison.Ordinal);
            int work = source.IndexOf("await MarkingHandshakeAsync", gate, StringComparison.Ordinal);
            Assert.True(register >= 0 && pause > register && gate > pause && work > gate);
        }
        else
        {
            int register = source.LastIndexOf("action.RegisterLock(\"ZoneTS\");", gate, StringComparison.Ordinal);
            int recheck = source.IndexOf("if (!RackHasPhysicalPlateSnapshot(rs))", gate, StringComparison.Ordinal);
            Assert.True(register >= 0 && gate > register && recheck > gate);
            Assert.Contains("validateAction: operation.ThrowIfInvalid", call);
        }
        // 一个入口一处检查：本次不扩展到后端下料或前端后续ZoneTS入口。
        Assert.Equal(1, source.Split("await CranePeerXGate.WaitAsync(").Length - 1);
    }
}
