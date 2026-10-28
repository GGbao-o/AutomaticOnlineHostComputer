using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Tests.Communication;

/// <summary>Modbus使用127.0.0.1模拟PLC；FANUC只检查托管快照和Worker接入，不访问原生SDK。</summary>
public sealed class SkewDoorSignalSnapshotTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(-1, false)]
    [InlineData(32767, false)]
    public async Task Modbus_reads_through_10359_once_and_keeps_other_five_flags_nonzero_based(int door, bool expected)
    {
        await Exchange(1, (_, request) =>
        {
            Assert.Equal(new byte[] { 3, 0x28, 0x70, 0, 8 }, request); // 10352起读8Word，含10359。
            return Words(door);
        }, async service =>
        {
            var result = await service.ReadSignalsAsync();
            Assert.True(result.rqData);
            Assert.True(result.rqLoad);
            Assert.True(result.clamped);
            Assert.True(result.rqUnload);
            Assert.True(result.opened); // 尾座已张开也不能替代门开许可。
            Assert.Equal(expected, result.doorOpen);
        });
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(-1, false)]
    public async Task Standalone_modbus_door_read_also_requires_exact_one(int value, bool expected)
    {
        await Exchange(1, (_, request) =>
        {
            Assert.Equal(new byte[] { 3, 0x28, 0x77, 0, 1 }, request); // 10359。
            return new[] { unchecked((ushort)value) };
        }, async service => Assert.Equal(expected, await service.IsDoorOpenAsync()));
    }

    [Fact]
    public async Task Short_replies_after_an_open_snapshot_cannot_reuse_previous_door_permission()
    {
        // 第一次完整成功，随后三次缺少10359，走原设备重连重试后仍必须抛出。
        await Exchange(4, (index, request) =>
        {
            Assert.Equal(new byte[] { 3, 0x28, 0x70, 0, 8 }, request);
            return index == 0 ? Words(1) : Words(1)[..7];
        }, async service =>
        {
            Assert.True((await service.ReadSignalsAsync()).doorOpen);
            await Assert.ThrowsAnyAsync<Exception>(() => service.ReadSignalsAsync());
        });
    }

    [Fact]
    public void Fanuc_error_snapshot_clears_previous_open_door_without_native_sdk_calls()
    {
        using var service = new FanucSkewBedService("127.0.0.1");
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(FanucSkewBedService).GetField("_latestSnapshot", flags)!.SetValue(service,
            new FanucSignalSnapshot { Connected = true, DoorOpen = true, Opened = true });
        typeof(FanucSkewBedService).GetMethod("SetSnapshotError", flags)!.Invoke(service, new object[] { "门读取失败" });
        Assert.False(service.LatestSnapshot.Connected);
        Assert.False(service.LatestSnapshot.DoorOpen);
        Assert.Contains("门读取失败", service.LatestSnapshot.Error);
        Assert.False(service.IsWorkerAlive);
    }

    [Fact]
    public void Fanuc_poll_and_explicit_snapshot_read_the_door_on_the_existing_worker_with_exact_one()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Communication", "DeviceServices", "FanucSkewBedService.cs"));
        int poll = source.IndexOf("private bool PollSnapshotOnce", StringComparison.Ordinal);
        int readDoor = source.IndexOf("double doorOpenRaw = _sdk.GetMacro(FanucSkewBedAddress.AutoDoorOpen)", poll, StringComparison.Ordinal);
        int publish = source.IndexOf("_latestSnapshot = new FanucSignalSnapshot", poll, StringComparison.Ordinal);
        Assert.True(readDoor > poll && publish > readDoor);
        Assert.Contains("DoorOpen = doorOpenRaw == 1.0", source[poll..source.IndexOf("private void SetSnapshotError", poll, StringComparison.Ordinal)]);
        int explicitRead = source.IndexOf("public Task<(bool rqData", StringComparison.Ordinal);
        int explicitEnd = source.IndexOf("public Task<bool> IsMachineReadyAsync", explicitRead, StringComparison.Ordinal);
        string block = source[explicitRead..explicitEnd];
        Assert.Contains("return SafeCallAsync(() =>", block);
        Assert.Contains("_sdk.GetMacro(FanucSkewBedAddress.AutoDoorOpen) == 1.0", block);
        Assert.Contains("return (rqData, rqLoad, clamped, rqUnload, opened, doorOpen)", block);
        // 本轮没有为门开读取再创建线程，也不能从两线后端直接调用SDK。
        foreach (string file in new[] { "Line1RearFlowEngine.cs", "Line2RearFlowEngine.cs" })
        {
            string engine = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", file));
            Assert.DoesNotContain("_sdk.GetMacro", engine);
            Assert.Contains("doorOpen = snap.DoorOpen", engine);
        }
    }

    private static ushort[] Words(int door) => new ushort[] { 2, 0, ushort.MaxValue, 2, 0, ushort.MaxValue, 1, unchecked((ushort)door) };

    private static async Task Exchange(int exchanges, Func<int, byte[], ushort[]> reply, Func<ModbusSkewBedService, Task> action)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task server = Task.Run(async () =>
        {
            TcpClient? peer = null;
            try
            {
                for (int index = 0; index < exchanges; index++)
                {
                    byte[] header = new byte[7];
                    while (true)
                    {
                        peer ??= await listener.AcceptTcpClientAsync(timeout.Token);
                        try { await peer.GetStream().ReadExactlyAsync(header, timeout.Token); break; }
                        catch (EndOfStreamException) { peer.Dispose(); peer = null; }
                    }
                    var stream = peer.GetStream();
                    byte[] request = new byte[((header[4] << 8) | header[5]) - 1];
                    await stream.ReadExactlyAsync(request, timeout.Token);
                    ushort[] words = reply(index, request);
                    byte[] frame = new byte[9 + words.Length * 2];
                    Array.Copy(header, frame, 7);
                    frame[4] = (byte)((frame.Length - 6) >> 8);
                    frame[5] = (byte)(frame.Length - 6);
                    frame[7] = 3; frame[8] = (byte)(words.Length * 2);
                    for (int i = 0; i < words.Length; i++)
                    {
                        frame[9 + i * 2] = (byte)(words[i] >> 8);
                        frame[10 + i * 2] = (byte)words[i];
                    }
                    await stream.WriteAsync(frame, timeout.Token);
                }
            }
            finally { peer?.Dispose(); }
        });
        using var service = new ModbusSkewBedService("127.0.0.1", port);
        try
        {
            await action(service).WaitAsync(timeout.Token);
            await server.WaitAsync(timeout.Token);
        }
        finally { timeout.Cancel(); listener.Stop(); }
    }
}
