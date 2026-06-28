using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using Addr = AutomaticOnlineHostComputer.Communication.DeviceAddresses.BoringModbusAddress;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices;

/// <summary>
/// 双头镗 Modbus TCP 服务。
/// <para>R 区地址统一通过 R*2+1 转换；正常握手输出只写 1，不做脉冲和自动清零。</para>
/// </summary>
public sealed class BoringModbusService : IDisposable
{
    private readonly string _name;
    private readonly ModbusTcpClient _client;
    private bool _disposed;

    public BoringModbusService(string name, string ip, int port = 502)
    {
        _name = name;
        _client = new ModbusTcpClient(ip, port, unitId: 1, timeoutMs: 3000);
        Console.WriteLine($"[BoringModbusSvc] [{_name}] 创建实例 IP={ip}:{port}");
    }

    public Task ConnectAsync(CancellationToken ct = default) => _client.ConnectAsync(ct);
    public Task DisconnectAsync() => _client.DisconnectAsync();
    public bool IsConnected => _client.IsConnected;

    public async Task<BoringModbusStatusSnapshot> ReadAllSignalsAsync(CancellationToken ct = default)
    {
        int start = Addr.RToModbus(Addr.R_RequestData);
        int end = Addr.RToModbus(Addr.R_UnloadDone);
        var result = await _client.ReadAsync(start, end - start + 1, ct);

        int At(int rNumber)
        {
            int index = Addr.RToModbus(rNumber) - start;
            if ((uint)index >= (uint)result.IntValues.Length)
                throw new InvalidOperationException($"双头镗状态快照缺少 R{rNumber}。");
            return result.IntValues[index];
        }

        return new BoringModbusStatusSnapshot(
            At(Addr.R_RequestData) != 0,
            At(Addr.R_DataSentDone) != 0,
            At(Addr.R_RequestLoad) != 0,
            At(Addr.R_LoadDone) != 0,
            At(Addr.R_RequestUnload) != 0,
            At(Addr.R_UnloadDone) != 0);
    }

    public Task<bool> IsRequestDataAsync(CancellationToken ct = default)
        => ReadBoolAsync(Addr.R_RequestData, ct);

    public Task<bool> IsRequestLoadAsync(CancellationToken ct = default)
        => ReadBoolAsync(Addr.R_RequestLoad, ct);

    public Task<bool> IsRequestUnloadAsync(CancellationToken ct = default)
        => ReadBoolAsync(Addr.R_RequestUnload, ct);

    /// <summary>机械手取总上料架前的硬条件：双头镗在线且已经请求数据。</summary>
    public Task<bool> IsReadyForNextWorkpieceAsync(CancellationToken ct = default)
        => IsRequestDataAsync(ct);

    /// <summary>
    /// 下发双头镗加工参数。
    /// 长度按32位无符号整数写ERP原值；直径/堵厚/版孔按协议乘100写整数。R2046/R2048仅保留地址，当前不写。
    /// </summary>
    public async Task SendMachiningParamsAsync(
        double rollerLength,
        double outerDiameter,
        double leftPlugThickness,
        double rightPlugThickness,
        double boreType,
        CancellationToken ct = default)
    {
        uint rollerLengthUInt32 = ToUInt32(rollerLength, nameof(rollerLength));
        await _client.WriteUInt32Async(Addr.RToModbus(Addr.R_RollerLength), rollerLengthUInt32, ct);
        await _client.WriteAsync(Addr.RToModbus(Addr.R_OuterDiameter), Scale100(outerDiameter), ct);
        await _client.WriteAsync(Addr.RToModbus(Addr.R_LeftPlugThickness), Scale100(leftPlugThickness), ct);
        await _client.WriteAsync(Addr.RToModbus(Addr.R_RightPlugThickness), Scale100(rightPlugThickness), ct);
        await _client.WriteAsync(Addr.RToModbus(Addr.R_BoreType), Scale100(boreType), ct);
        Console.WriteLine(
            $"[BoringModbusSvc] [{_name}] 参数已写 R2041={rollerLengthUInt32}(uint32,ERP={rollerLength}) R2043={Scale100(outerDiameter)} R2044={Scale100(leftPlugThickness)} R2045={Scale100(rightPlugThickness)} R2047={Scale100(boreType)}");
    }

    public Task SetDataSentDoneAsync(CancellationToken ct = default)
        => WriteRAsync(Addr.R_DataSentDone, 1, ct);

    public Task SetLoadDoneAsync(CancellationToken ct = default)
        => WriteRAsync(Addr.R_LoadDone, 1, ct);

    public Task SetUnloadDoneAsync(CancellationToken ct = default)
        => WriteRAsync(Addr.R_UnloadDone, 1, ct);

    /// <summary>直接写 R 区寄存器。仅供人工清理/诊断使用，正常握手不要用它清零。</summary>
    public Task WriteRAsync(int rNumber, int value, CancellationToken ct = default)
        => _client.WriteAsync(Addr.RToModbus(rNumber), value, ct);

    private async Task<bool> ReadBoolAsync(int rNumber, CancellationToken ct)
    {
        var result = await _client.ReadAsync(Addr.RToModbus(rNumber), 1, ct);
        return result.IntValues.Length > 0 && result.IntValues[0] != 0;
    }

    private static int Scale100(double value)
        => (int)Math.Round(value * 100, MidpointRounding.AwayFromZero);

    private static uint ToUInt32(double value, string paramName)
    {
        if (!double.IsFinite(value) || value < 0 || value > uint.MaxValue)
            throw new ArgumentOutOfRangeException(paramName, value, "双头镗长度必须是有效的32位无符号整数范围。");
        return checked((uint)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

public sealed record BoringModbusStatusSnapshot(
    bool RequestData,
    bool DataSentDone,
    bool RequestLoad,
    bool LoadDone,
    bool RequestUnload,
    bool UnloadDone);
