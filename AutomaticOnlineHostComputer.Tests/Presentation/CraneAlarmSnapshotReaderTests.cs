using AutomaticOnlineHostComputer.Communication.DeviceAddresses;
using AutomaticOnlineHostComputer.Service.Alarms;

namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class CraneAlarmSnapshotReaderTests
{
    [Fact]
    public async Task Sample_decodes_signed_and_unsigned_axes_and_only_reads_documented_addresses()
    {
        var connection = new FakeConnection();
        var r = connection.Registers;
        r[18] = -2; r[19] = -1; // X=-2, low word before high word
        r[14] = 123; r[15] = 1;
        r[20] = -10; r[22] = -1; r[23] = -2; r[25] = 120;
        r[29] = 1; r[9] = 7; r[11] = 8; r[12] = 9; r[13] = 10;
        var reader = new CraneAlarmSnapshotReader(_ => connection, TimeSpan.FromSeconds(2));
        var sample = await reader.ReadAsync(4, CancellationToken.None);
        Assert.Equal(-2, sample.Position!.X);
        Assert.Equal(65659, sample.Position.AbsX);
        Assert.Equal(65535, sample.Position.Y);
        Assert.Equal(-10, sample.Position.AbsY);
        Assert.Equal(65534, sample.Position.AbsZ);
        Assert.Equal(120, sample.Position.Z);
        Assert.Equal(7, sample.Position.Fault);
        Assert.Equal(1, sample.Position.HasRoller);
        Assert.Equal(new[] { CraneAddress.D_X6_MagnetizeOk, CraneAddress.D_X7_DemagnetizeOk,
            CraneAddress.D_X11_HasPlate, CraneAddress.D_X2_MagnetLimit }, connection.InputAddresses);
        Assert.True(sample.X6.Value);
        Assert.NotNull(sample.X6.ReadAtUtc);
        Assert.True(connection.Disposed);
        Assert.Equal(1, connection.ConnectCount);
    }

    [Fact]
    public async Task Partial_read_failure_keeps_other_feedback_and_never_reconnects()
    {
        var connection = new FakeConnection { FailX7 = true };
        var reader = new CraneAlarmSnapshotReader(_ => connection, TimeSpan.FromSeconds(2));
        var sample = await reader.ReadAsync(2, CancellationToken.None);
        Assert.NotNull(sample.Position);
        Assert.Null(sample.X7.Value);
        Assert.Contains("X7读取失败", sample.X7.Error);
        Assert.True(sample.X6.Value);
        Assert.True(sample.X11.Value);
        Assert.Equal(1, connection.ConnectCount);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task Total_timeout_cancels_read_and_disposes_only_temporary_connection()
    {
        var connection = new FakeConnection { BlockRegisters = true };
        var reader = new CraneAlarmSnapshotReader(_ => connection, TimeSpan.FromMilliseconds(40));
        var sample = await reader.ReadAsync(5, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(sample.Position);
        Assert.Contains("总时限", sample.PositionError);
        Assert.Null(sample.X6.Value);
        Assert.Empty(connection.InputAddresses);
        Assert.True(connection.ReadCancelled);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task Missing_crane_configuration_reports_unknown_instead_of_zero()
    {
        var reader = new CraneAlarmSnapshotReader(_ => throw new InvalidOperationException("无映射"), TimeSpan.FromSeconds(2));
        var sample = await reader.ReadAsync(99, CancellationToken.None);
        Assert.Null(sample.Position);
        Assert.Null(sample.X11.Value);
        Assert.Contains("无映射", sample.X11.Error);
    }

    private sealed class FakeConnection : IAlarmReadConnection
    {
        public int[] Registers { get; } = new int[30];
        public List<int> InputAddresses { get; } = new();
        public bool FailX7 { get; init; }
        public bool BlockRegisters { get; init; }
        public bool ReadCancelled { get; private set; }
        public bool Disposed { get; private set; }
        public int ConnectCount { get; private set; }
        public Task ConnectAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); ConnectCount++; return Task.CompletedTask; }
        public async Task<int[]> ReadRegistersAsync(CancellationToken ct)
        {
            if (BlockRegisters)
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { ReadCancelled = true; throw; }
            return Registers;
        }
        public Task<bool> ReadInputAsync(int address, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            InputAddresses.Add(address);
            if (FailX7 && address == CraneAddress.D_X7_DemagnetizeOk) throw new InvalidOperationException("X7读取失败");
            return Task.FromResult(true);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
