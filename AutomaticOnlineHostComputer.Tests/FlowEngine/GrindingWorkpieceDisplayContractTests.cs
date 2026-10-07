using AutomaticOnlineHostComputer.Communication.Contracts;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Communication.Models;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Config;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class GrindingWorkpieceDisplayContractTests
{
    [Fact]
    public void Grinding_display_ip_is_exposed_on_configuration_page()
    {
        string model = Read("Infrastructure", "Config", "MotionConfig.cs");
        string json = Read("Config", "motion_settings.json");
        string viewModel = Read("Presentation", "ViewModels", "Config", "ConfigPageViewModel.cs");
        string view = Read("Views", "Config", "ConfigPageView.xaml");

        Assert.Contains("public string WorkpieceDisplayIp { get; set; } = \"192.168.2.111\";", model, StringComparison.Ordinal);
        Assert.Contains("\"workpieceDisplayIp\": \"192.168.2.111\"", json, StringComparison.Ordinal);
        Assert.Contains("public string GrindingWorkpieceDisplayIp", viewModel, StringComparison.Ordinal);
        Assert.Contains("_cfg.Grinding.WorkpieceDisplayIp", viewModel, StringComparison.Ordinal);
        Assert.Contains("工件显示屏 IP", view, StringComparison.Ordinal);
        Assert.Contains("GrindingWorkpieceDisplayIp", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_page_updates_shared_grinding_display_ip()
    {
        var config = new MotionConfig();
        var viewModel = new ConfigPageViewModel(config);

        viewModel.GrindingWorkpieceDisplayIp = "192.168.2.222";

        Assert.Equal("192.168.2.222", config.Grinding.WorkpieceDisplayIp);
        Assert.Equal("192.168.2.222", viewModel.GrindingWorkpieceDisplayIp);
    }

    [Fact]
    public async Task Display_writes_rounded_values_to_fixed_registers()
    {
        var fake = new FakeDeviceClient();
        (string Ip, int Port, byte UnitId, int TimeoutMs)? created = null;
        await using var service = new GrindingWorkpieceDisplayService((ip, port, unitId, timeoutMs) =>
        {
            created = (ip, port, unitId, timeoutMs);
            return fake;
        });

        var result = await service.TryWriteAsync("192.168.2.111", 159.6, 1000.4);

        Assert.True(result.Success);
        Assert.Equal(("192.168.2.111", 502, (byte)1, 3000), created!.Value);
        Assert.Equal(new[] { (100, 160), (101, 1000) }, fake.Writes);
        Assert.Equal(1, fake.ConnectCount);
    }

    [Fact]
    public async Task Display_reuses_same_ip_and_replaces_client_after_ip_change()
    {
        var clients = new List<FakeDeviceClient>();
        await using var service = new GrindingWorkpieceDisplayService((_, _, _, _) =>
        {
            var client = new FakeDeviceClient();
            clients.Add(client);
            return client;
        });

        Assert.True((await service.TryWriteAsync("192.168.2.111", 160, 1000)).Success);
        Assert.True((await service.TryWriteAsync("192.168.2.111", 161, 1001)).Success);
        Assert.True((await service.TryWriteAsync("192.168.2.112", 162, 1002)).Success);

        Assert.Equal(2, clients.Count);
        Assert.Equal(1, clients[0].ConnectCount);
        Assert.Equal(1, clients[0].DisposeCount);
        Assert.Equal(1, clients[1].ConnectCount);
    }

    [Fact]
    public async Task Display_write_failure_returns_failure_and_discards_connection()
    {
        var fake = new FakeDeviceClient { WriteFailureAddress = 101 };
        await using var service = new GrindingWorkpieceDisplayService((_, _, _, _) => fake);

        var result = await service.TryWriteAsync("192.168.2.111", 160, 1000);

        Assert.False(result.Success);
        Assert.Equal("长度地址101", result.Stage);
        Assert.IsType<IOException>(result.Error);
        Assert.Equal(1, fake.DisposeCount);
    }

    [Fact]
    public async Task Display_preserves_caller_cancellation()
    {
        var fake = new FakeDeviceClient { CancelDuringConnect = true };
        await using var service = new GrindingWorkpieceDisplayService((_, _, _, _) => fake);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TryWriteAsync("192.168.2.111", 160, 1000, cts.Token));
    }

    [Fact]
    public void Grinding_engine_writes_display_after_final_checks_and_before_crane_motion_setup()
    {
        string source = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");
        string method = ExtractMethod(source, "private async Task ProcessWorkpieceAsync");

        int supplementedPending = method.IndexOf(
            "grinder.PendingWorkpiece = wp; // 补长后的数据同步到Pending", StringComparison.Ordinal);
        int x11Check = method.IndexOf(
            "hasExistingWorkpiece = await crane.ReadXBitAsync", StringComparison.Ordinal);
        int actionM730 = method.IndexOf(
            "ConfirmGrindingFeedReadyAsync(\"上料动作前\"", StringComparison.Ordinal);
        int displayWrite = method.IndexOf(
            "WriteWorkpieceDisplayBestEffortAsync(wp, ct)", StringComparison.Ordinal);
        int coordinateLookup = method.IndexOf(
            "TryGetStationCoords(\"ST709\"", StringComparison.Ordinal);
        int speedSetup = method.IndexOf(
            "await crane.SetAbsSpeedAsync", StringComparison.Ordinal);

        Assert.True(supplementedPending >= 0, "未找到补长后Pending同步");
        Assert.True(x11Check > supplementedPending, "X11检查必须晚于最终Pending数据形成");
        Assert.True(actionM730 > x11Check, "动作前M730确认必须晚于X11无残留检查");
        Assert.True(displayWrite > actionM730, "显示屏写入必须晚于动作前M730确认");
        Assert.True(coordinateLookup > displayWrite, "显示屏写入必须早于ST709坐标与运动准备");
        Assert.True(speedSetup > displayWrite, "显示屏写入必须早于天车速度设置");
    }

    [Fact]
    public void Grinding_display_helper_is_log_only_and_does_not_mutate_business_state()
    {
        string source = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");
        string helper = ExtractMethod(source, "private async Task WriteWorkpieceDisplayBestEffortAsync");
        string dispose = ExtractMethod(source, "public void Dispose()");

        Assert.Contains("_workpieceDisplay.TryWriteAsync", helper, StringComparison.Ordinal);
        Assert.Contains("写入失败但继续取料", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("_paused", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("OnSafetyAlarm", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("RequeueWorkpiece", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("PendingWorkpiece", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("State =", helper, StringComparison.Ordinal);
        Assert.Contains("_workpieceDisplay.DisposeAsync", dispose, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(parts)));

    private static string ExtractMethod(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到方法: {signature}");
        int nextSummary = source.IndexOf("    /// <summary>", start + signature.Length, StringComparison.Ordinal);
        return nextSummary >= 0 ? source[start..nextSummary] : source[start..];
    }

    private sealed class FakeDeviceClient : IDeviceClient
    {
        public bool IsConnected { get; private set; }
        public int ConnectCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int? WriteFailureAddress { get; init; }
        public bool CancelDuringConnect { get; init; }
        public List<(int Address, int Value)> Writes { get; } = new();

        public Task ConnectAsync(CancellationToken ct = default)
        {
            ConnectCount++;
            if (CancelDuringConnect) ct.ThrowIfCancellationRequested();
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task WriteAsync(int address, int value, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (WriteFailureAddress == address)
                throw new IOException($"模拟地址{address}写入失败");
            Writes.Add((address, value));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }

        public Task<ReadResult> ReadAsync(int address, int count = 1, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<double> ReadDoubleAsync(int address, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> ReadIntAsync(int address, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task WriteAsync(int address, double value, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task WriteBatchAsync(int[] addresses, int[] values, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task WriteBatchAsync(int[] addresses, double[] values, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
