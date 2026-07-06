using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using Addr = AutomaticOnlineHostComputer.Communication.DeviceAddresses.BoringModbusAddress;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices;

/// <summary>
/// 双头镗 Modbus TCP 服务。
/// <para>R 区地址统一通过 R*2+1 转换；下料完成时按协议写R6108=1后立即清R6102/R6104，R6108保持为1。</para>
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
        var raw = await ReadCycleRawAsync(ct);
        return new BoringModbusStatusSnapshot(
            raw.R6101 != 0,
            raw.R6102 != 0,
            raw.R6103 != 0,
            raw.R6104 != 0,
            raw.R6107 != 0,
            raw.R6108 != 0);
    }

    /// <summary>
    /// 实时读取双头镗本轮握手区 R6101~R6108 的原始整数值。
    /// R6105/R6106 当前没有上位机业务定义，但应急诊断仍显示原值，
    /// 防止未定义位被现场PLC逻辑使用时遗漏关键信息。
    /// </summary>
    public async Task<BoringCycleRawSnapshot> ReadCycleRawAsync(CancellationToken ct = default)
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

        return new BoringCycleRawSnapshot(
            At(6101), At(6102), At(6103), At(6104),
            At(6105), At(6106), At(6107), At(6108));
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
    /// 长度按16位无符号整数写ERP原值；直径/堵厚/内孔锥度/版孔/圆角按协议乘100写整数。
    /// </summary>
    public async Task SendMachiningParamsAsync(
        double rollerLength,
        double outerDiameter,
        double leftPlugThickness,
        double rightPlugThickness,
        double innerTaper,
        double boreType,
        double cornerSize,
        CancellationToken ct = default)
    {
        ushort rollerLengthUInt16 = ToUInt16(rollerLength, nameof(rollerLength));
        ushort innerTaperUInt16 = Scale100ToUInt16(innerTaper, nameof(innerTaper));
        ushort cornerSizeUInt16 = Scale100ToUInt16(cornerSize, nameof(cornerSize));
        await _client.WriteAsync(Addr.RToModbus(Addr.R_RollerLength), rollerLengthUInt16, ct);
        await _client.WriteAsync(Addr.RToModbus(Addr.R_OuterDiameter), Scale100(outerDiameter), ct);
        await _client.WriteAsync(Addr.RToModbus(Addr.R_LeftPlugThickness), Scale100(leftPlugThickness), ct);
        await _client.WriteAsync(Addr.RToModbus(Addr.R_RightPlugThickness), Scale100(rightPlugThickness), ct);
        await _client.WriteAsync(Addr.RToModbus(Addr.R_InnerTaper), innerTaperUInt16, ct);
        await _client.WriteAsync(Addr.RToModbus(Addr.R_BoreType), Scale100(boreType), ct);
        await _client.WriteAsync(Addr.RToModbus(Addr.R_CornerSize), cornerSizeUInt16, ct);
        Console.WriteLine(
            $"[BoringModbusSvc] [{_name}] 参数已写 R2041={rollerLengthUInt16}(uint16,ERP={rollerLength}) " +
            $"R2043={Scale100(outerDiameter)} R2044={Scale100(leftPlugThickness)} R2045={Scale100(rightPlugThickness)} " +
            $"R2046={innerTaperUInt16}(内孔锥度={innerTaper}) R2047={Scale100(boreType)} " +
            $"R2048={cornerSizeUInt16}(圆角大小={cornerSize})");
    }

    public Task SetDataSentDoneAsync(CancellationToken ct = default)
        => WriteRAsync(Addr.R_DataSentDone, 1, ct);

    public Task SetLoadDoneAsync(CancellationToken ct = default)
        => WriteRAsync(Addr.R_LoadDone, 1, ct);

    public Task SetUnloadDoneAsync(CancellationToken ct = default)
        => WriteRAsync(Addr.R_UnloadDone, 1, ct);

    /// <summary>
    /// 应急清除上位机拥有的双头镗输出，保持现场既定顺序：
    /// R6108 下料完成 → R6104 上料完成 → R6102 数据完成。
    /// 不写 R6101/R6103/R6107 等 CNC→上位机输入。
    /// </summary>
    public async Task ClearEmergencyOutputsAsync(CancellationToken ct = default)
    {
        await WriteRAsync(Addr.R_UnloadDone, 0, ct);
        await WriteRAsync(Addr.R_LoadDone, 0, ct);
        await WriteRAsync(Addr.R_DataSentDone, 0, ct);
        Console.WriteLine($"[BoringModbusSvc] [{_name}] 应急清除 R6108/R6104/R6102→0");
    }

    /// <summary>
    /// 通知本轮下料完成，并立即清除本轮数据下发/上料完成握手位。
    /// 顺序固定为 R6108=1 → R6102=0 → R6104=0，R6108保持为1，不等待设备反馈。
    /// </summary>
    public async Task SetUnloadDoneAndClearCycleAsync(CancellationToken ct = default)
    {
        await WriteRAsync(Addr.R_UnloadDone, 1, ct);
        await WriteRAsync(Addr.R_DataSentDone, 0, ct);
        await WriteRAsync(Addr.R_LoadDone, 0, ct);
        Console.WriteLine($"[BoringModbusSvc] [{_name}] R6108=1并保持，已清零 R6102/R6104");
    }

    /// <summary>直接写 R 区寄存器，供协议封装及人工清理/诊断使用。</summary>
    public Task WriteRAsync(int rNumber, int value, CancellationToken ct = default)
        => _client.WriteAsync(Addr.RToModbus(rNumber), value, ct);

    private async Task<bool> ReadBoolAsync(int rNumber, CancellationToken ct)
    {
        var result = await _client.ReadAsync(Addr.RToModbus(rNumber), 1, ct);
        return result.IntValues.Length > 0 && result.IntValues[0] != 0;
    }

    private static int Scale100(double value)
        => (int)Math.Round(value * 100, MidpointRounding.AwayFromZero);

    private static ushort Scale100ToUInt16(double value, string paramName)
    {
        if (!double.IsFinite(value) || value <= 0 || value > 655.35)
            throw new ArgumentOutOfRangeException(paramName, value, "双头镗参数必须>0且<=655.35。");
        return checked((ushort)Math.Round(value * 100, MidpointRounding.AwayFromZero));
    }

    private static ushort ToUInt16(double value, string paramName)
    {
        if (!double.IsFinite(value) || value < 0 || value > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(paramName, value, "双头镗长度必须是有效的16位无符号整数范围。");
        return checked((ushort)Math.Round(value, MidpointRounding.AwayFromZero));
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

/// <summary>双头镗本轮握手区 R6101~R6108 原始值，仅用于实时诊断和人工恢复判断。</summary>
public sealed record BoringCycleRawSnapshot(
    int R6101,
    int R6102,
    int R6103,
    int R6104,
    int R6105,
    int R6106,
    int R6107,
    int R6108);
