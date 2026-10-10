using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;
using Xunit;

namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class ForkManualControlViewModelTests
{
    [Theory]
    [InlineData(0, 911)]
    [InlineData(0, 912)]
    [InlineData(0, 913)]
    [InlineData(0, 914)]
    [InlineData(1, 911)]
    [InlineData(1, 912)]
    [InlineData(1, 913)]
    [InlineData(1, 914)]
    public async Task Each_action_routes_once_to_selected_station(int lineIndex, int address)
    {
        var created = new List<(string Name, string Ip, int Port, FakeDevice Device)>();
        using var vm = new ForkManualControlViewModel((name, ip, port) =>
        {
            var device = new FakeDevice();
            created.Add((name, ip, port, device));
            return device;
        });
        vm.LoadMappings(Rows());
        vm.SelectedLine = vm.LineItems[lineIndex];

        await SendAsync(vm, address);

        var target = Assert.Single(created);
        Assert.Contains(lineIndex == 0 ? "ST711" : "ST712", target.Name);
        Assert.Equal(lineIndex == 0 ? "10.0.0.11" : "10.0.0.12", target.Ip);
        Assert.Equal(lineIndex == 0 ? 9101 : 9000, target.Port);
        Assert.Equal(new[] { address }, target.Device.Commands);
        Assert.Equal(1, target.Device.ConnectCount);
        Assert.Equal(1, target.Device.ReadCount);
        Assert.Contains("命令已发送", vm.LastOperationResult);
        Assert.DoesNotContain("动作完成", vm.LastOperationResult);
    }

    [Fact]
    public async Task Refresh_only_reads_and_clients_are_reused_per_line()
    {
        var devices = new List<FakeDevice>();
        using var vm = new ForkManualControlViewModel((_, _, _) =>
        {
            var device = new FakeDevice();
            devices.Add(device);
            return device;
        });
        vm.LoadMappings(Rows());
        await vm.RefreshAsync();
        await vm.RefreshAsync();
        vm.SelectedLine = vm.LineItems[1];
        Assert.Null(vm.HasPlate);
        Assert.Null(vm.LastRefreshedAt);
        await vm.RefreshAsync();
        vm.SelectedLine = vm.LineItems[0];
        await vm.RefreshAsync();

        Assert.Equal(2, devices.Count);
        Assert.Equal(3, devices[0].ReadCount);
        Assert.All(devices, d => { Assert.Empty(d.Commands); Assert.Equal(1, d.ConnectCount); });
        Assert.True(vm.HasPlate);
        Assert.True(vm.AtStandbyPos);
        Assert.False(vm.AtPos3);
    }

    [Fact]
    public async Task In_flight_request_blocks_switching_and_other_commands()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new FakeDevice { SendCompletion = completion };
        using var vm = Create(device);
        var request = vm.GoStandbyAsync();

        Assert.True(vm.IsBusy);
        Assert.False(vm.CanOperate);
        Assert.False(vm.FeedToBoringCommand.CanExecute(null));
        Assert.False(vm.RefreshStatusCommand.CanExecute(null));
        vm.SelectedLine = vm.LineItems[1];
        await vm.GoFeedToBoringAsync();
        await vm.RefreshAsync();
        Assert.Equal(1, vm.SelectedLine.LineNo);
        Assert.Equal(new[] { 911 }, device.Commands);
        Assert.Equal(0, device.ReadCount);

        completion.SetResult();
        await request;
        Assert.True(vm.CanOperate);
        Assert.True(vm.FeedToBoringCommand.CanExecute(null));
        vm.SelectedLine = vm.LineItems[1];
        Assert.Equal(2, vm.SelectedLine.LineNo);
        Assert.Null(vm.HasPlate);
    }

    [Fact]
    public async Task Failed_write_is_not_replayed_or_reported_as_success()
    {
        var device = new FakeDevice { SendException = new IOException("应答丢失") };
        using var vm = Create(device);
        await vm.RefreshAsync();
        await vm.GoStandbyAsync();

        Assert.Equal(new[] { 911 }, device.Commands);
        Assert.Equal(1, device.ReadCount);
        Assert.Null(vm.HasPlate);
        Assert.Null(vm.LastRefreshedAt);
        Assert.Contains("发送未确认", vm.LastOperationResult);
        Assert.DoesNotContain("命令已发送", vm.LastOperationResult);
        Assert.True(vm.CanOperate);
    }

    [Fact]
    public async Task Read_failure_after_write_keeps_send_success_and_unknown_feedback()
    {
        var device = new FakeDevice { ReadException = new IOException("状态读取失败") };
        using var vm = Create(device);
        await vm.GoPickFromBoringAsync();

        Assert.Equal(new[] { 913 }, device.Commands);
        Assert.Contains("命令已发送", vm.LastOperationResult);
        Assert.Contains("状态刷新失败", vm.LastOperationResult);
        Assert.Null(vm.HasPlate);
        Assert.Null(vm.AtStandbyPos);
        Assert.Null(vm.AtPos3);
        Assert.Null(vm.LastRefreshedAt);
    }

    [Fact]
    public async Task Refresh_failure_clears_previous_feedback_without_sending()
    {
        var device = new FakeDevice();
        using var vm = Create(device);
        await vm.RefreshAsync();
        device.ReadException = new IOException("断线");
        await vm.RefreshAsync();
        Assert.Null(vm.HasPlate);
        Assert.Empty(device.Commands);
        Assert.Contains("状态刷新失败", vm.LastOperationResult);
    }

    [Fact]
    public async Task Missing_mapping_never_creates_or_connects_a_device()
    {
        int created = 0;
        using var vm = new ForkManualControlViewModel((_, _, _) => { created++; return new FakeDevice(); });
        await vm.GoStandbyAsync();
        Assert.Equal(0, created);
        Assert.Contains("ST711", vm.LastOperationResult);
        Assert.Contains("未配置", vm.LastOperationResult);
        Assert.Null(vm.HasPlate);
    }

    [Fact]
    public async Task Changed_mapping_replaces_only_manual_client_and_dispose_releases_clients()
    {
        var devices = new List<FakeDevice>();
        var ips = new List<string>();
        var vm = new ForkManualControlViewModel((_, ip, _) =>
        {
            ips.Add(ip);
            var device = new FakeDevice();
            devices.Add(device);
            return device;
        });
        vm.LoadMappings(Rows());
        await vm.RefreshAsync();
        var rows = Rows();
        rows[0].Ip = "10.0.0.21";
        vm.LoadMappings(rows);
        Assert.Null(vm.HasPlate);
        await vm.RefreshAsync();
        Assert.True(devices[0].Disposed);
        Assert.Equal(new[] { "10.0.0.11", "10.0.0.21" }, ips);
        vm.Dispose();
        vm.Dispose();
        Assert.All(devices, d => Assert.True(d.Disposed));
        Assert.False(vm.RefreshStatusCommand.CanExecute(null));
        await vm.GoStandbyAsync();
        Assert.All(devices, d => Assert.Empty(d.Commands));
    }

    [Fact]
    public async Task Connect_failure_sends_nothing_and_releases_busy_state()
    {
        var device = new FakeDevice { ConnectException = new IOException("无法连接") };
        using var vm = Create(device);
        await vm.GoFeedToBoringAsync();
        Assert.Empty(device.Commands);
        Assert.Equal(0, device.ReadCount);
        Assert.Contains("连接失败", vm.LastOperationResult);
        Assert.DoesNotContain("命令已发送", vm.LastOperationResult);
        Assert.Null(vm.HasPlate);
        Assert.True(vm.CanOperate);
    }

    [Fact]
    public async Task Reload_during_send_does_not_route_to_new_endpoint_or_publish_old_feedback()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new FakeDevice { SendCompletion = completion };
        var ips = new List<string>();
        using var vm = new ForkManualControlViewModel((_, ip, _) => { ips.Add(ip); return device; });
        vm.LoadMappings(Rows());
        var request = vm.GoStandbyToPos3Async();
        var rows = Rows();
        rows[0].Ip = "10.0.0.21";
        vm.LoadMappings(rows);
        completion.SetResult();
        await request;

        Assert.Equal(new[] { "10.0.0.11" }, ips);
        Assert.Equal(new[] { 914 }, device.Commands);
        Assert.Null(vm.HasPlate);
        Assert.Null(vm.LastRefreshedAt);
        Assert.Contains("命令已发送", vm.LastOperationResult);
        Assert.Contains("工位资料已更新", vm.LastOperationResult);
        Assert.Contains("10.0.0.21", vm.ConnectionStatus);
    }

    [Fact]
    public async Task Dispose_cancels_pending_request_without_replaying_it()
    {
        var device = new FakeDevice { SendCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = Create(device);
        var request = vm.GoStandbyAsync();
        Assert.True(vm.IsBusy);
        vm.Dispose();
        await request;
        Assert.True(device.Disposed);
        Assert.Equal(new[] { 911 }, device.Commands);
        Assert.False(vm.CanOperate);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.HasPlate);
    }

    private static ForkManualControlViewModel Create(FakeDevice device)
    {
        var vm = new ForkManualControlViewModel((_, _, _) => device);
        vm.LoadMappings(Rows());
        return vm;
    }

    private static MachineManagementRowVm[] Rows() =>
    [
        new() { StationCode = " st711 ", Ip = " 10.0.0.11 ", Port = 9101 },
        new() { StationCode = "ST712", Ip = "10.0.0.12", Port = 0 }
    ];

    private static Task SendAsync(ForkManualControlViewModel vm, int address) => address switch
    {
        911 => vm.GoStandbyAsync(),
        912 => vm.GoFeedToBoringAsync(),
        913 => vm.GoPickFromBoringAsync(),
        914 => vm.GoStandbyToPos3Async(),
        _ => throw new ArgumentOutOfRangeException(nameof(address))
    };

    private sealed class FakeDevice : IForkManualDevice
    {
        public bool IsConnected { get; private set; }
        public int ConnectCount { get; private set; }
        public int ReadCount { get; private set; }
        public List<int> Commands { get; } = [];
        public Exception? SendException { get; init; }
        public Exception? ConnectException { get; init; }
        public Exception? ReadException { get; set; }
        public TaskCompletionSource? SendCompletion { get; init; }
        public bool Disposed { get; private set; }
        public Task ConnectAsync(CancellationToken ct)
        {
            ConnectCount++;
            if (ConnectException != null) throw ConnectException;
            IsConnected = true;
            return Task.CompletedTask;
        }
        public Task<ForkStatus> ReadAllStatusAsync(CancellationToken ct)
        {
            ReadCount++;
            if (ReadException != null) throw ReadException;
            return Task.FromResult(new ForkStatus { HasPlate = true, AtStandbyPos = true });
        }
        private async Task SendAsync(int address, CancellationToken ct)
        {
            Commands.Add(address);
            if (SendCompletion != null) await SendCompletion.Task.WaitAsync(ct);
            if (SendException != null) throw SendException;
        }
        public Task GoStandbyAsync(CancellationToken ct) => SendAsync(911, ct);
        public Task GoFeedToBoringAsync(CancellationToken ct) => SendAsync(912, ct);
        public Task GoPickFromBoringAsync(CancellationToken ct) => SendAsync(913, ct);
        public Task GoStandbyToPos3Async(CancellationToken ct) => SendAsync(914, ct);
        public void Dispose() { Disposed = true; IsConnected = false; }
    }
}
