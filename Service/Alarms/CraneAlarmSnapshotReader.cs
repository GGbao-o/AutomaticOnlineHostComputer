using AutomaticOnlineHostComputer.Communication.Clients;
using System.IO;
using AutomaticOnlineHostComputer.Communication.DeviceAddresses;

namespace AutomaticOnlineHostComputer.Service.Alarms;

// 展示层只暴露读取接口；临时连接不访问生产服务的重连/安全恢复逻辑。
internal interface IAlarmReadConnection : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct);
    Task<int[]> ReadRegistersAsync(CancellationToken ct);
    Task<bool> ReadInputAsync(int address, CancellationToken ct);
}

internal sealed class CraneAlarmSnapshotReader
{
    private readonly Func<int, IAlarmReadConnection> _create;
    private readonly TimeSpan _budget;

    public CraneAlarmSnapshotReader(Func<int, (string Name, string Ip)> mapping)
        : this(no => new ModbusAlarmReadConnection(mapping(no).Ip), TimeSpan.FromSeconds(2)) { }

    internal CraneAlarmSnapshotReader(Func<int, IAlarmReadConnection> create, TimeSpan budget)
    {
        _create = create;
        _budget = budget;
    }

    public async Task<CraneAlarmSnapshot> ReadAsync(int craneNo, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_budget);
        var token = limit.Token;
        CraneAlarmPosition? position = null;
        string positionError = "尚未读取";
        var x2 = AlarmInputValue.Unknown("尚未读取");
        var x6 = x2; var x7 = x2; var x11 = x2;
        IAlarmReadConnection? connection = null;
        try
        {
            connection = _create(craneNo);
            await connection.ConnectAsync(token).ConfigureAwait(false);
            try
            {
                var r = await connection.ReadRegistersAsync(token).ConfigureAwait(false);
                if (r.Length < 30) throw new InvalidDataException("D5000状态返回不足30个寄存器");
                int Int32(int lo, int hi) => unchecked((int)((uint)(ushort)lo | ((uint)(ushort)hi << 16)));
                position = new(Int32(r[18], r[19]), (ushort)r[22], (ushort)r[25],
                    Int32(r[14], r[15]), (short)r[20], (ushort)r[23], (short)r[29],
                    (short)r[9], (short)r[11], (short)r[12], (short)r[13], (short)r[10], DateTime.UtcNow);
                positionError = string.Empty;
            }
            catch (Exception ex) { positionError = Error(ex); }

            async Task<AlarmInputValue> Input(int address)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    bool value = await connection.ReadInputAsync(address, token).ConfigureAwait(false);
                    return new(value, DateTime.UtcNow, string.Empty);
                }
                catch (Exception ex) { return AlarmInputValue.Unknown(Error(ex)); }
            }

            x6 = await Input(CraneAddress.D_X6_MagnetizeOk).ConfigureAwait(false);
            x7 = await Input(CraneAddress.D_X7_DemagnetizeOk).ConfigureAwait(false);
            x11 = await Input(CraneAddress.D_X11_HasPlate).ConfigureAwait(false);
            x2 = await Input(CraneAddress.D_X2_MagnetLimit).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            positionError = Error(ex);
            x2 = x6 = x7 = x11 = AlarmInputValue.Unknown(positionError);
        }
        finally
        {
            if (connection != null)
                try { await connection.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        return new(started, DateTime.UtcNow, position, positionError, x2, x6, x7, x11);
    }

    private static string Error(Exception ex) => ex is OperationCanceledException
        ? "采样已取消或超过总时限" : $"{ex.GetType().Name}：{ex.Message}";

    private sealed class ModbusAlarmReadConnection : IAlarmReadConnection
    {
        private readonly ModbusTcpClient _client;
        public ModbusAlarmReadConnection(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) throw new InvalidOperationException("未配置该天车IP");
            _client = new ModbusTcpClient(ip);
        }
        public Task ConnectAsync(CancellationToken ct) => _client.ConnectAsync(ct);
        public async Task<int[]> ReadRegistersAsync(CancellationToken ct) =>
            (await _client.ReadAsync(5000, 30, ct).ConfigureAwait(false)).IntValues;
        public Task<bool> ReadInputAsync(int address, CancellationToken ct) => _client.ReadCoilAsync(address, ct);
        public ValueTask DisposeAsync() => _client.DisposeAsync();
    }
}
