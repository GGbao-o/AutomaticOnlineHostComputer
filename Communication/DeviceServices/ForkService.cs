using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using Addr = AutomaticOnlineHostComputer.Communication.DeviceAddresses.ForkAddress;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices;

/// <summary>
/// 货叉通信服务（三菱 MC 协议，M 寄存器，端口 9000）。
/// <para>
/// 货叉由三菱 FX3G PLC 独立控制，3 个气缸完成伸缩动作。
/// M900~M906 为输入信号（货叉→上位机），M911~M916 为输出信号（上位机→货叉）。
/// </para>
/// </summary>
public sealed class ForkService : IDisposable
{
    private readonly MitsubishiMcClient _client;
    private readonly string _name;
    private bool _disposed;

    /// <param name="name">调试名称</param>
    /// <param name="ip">三菱 PLC IP</param>
    /// <param name="port">MC 协议端口，默认 9000</param>
    public ForkService(string name, string ip, int port = 9000)
    {
        _name = name;
        _client = new MitsubishiMcClient(ip, port, MitsubishiMcClient.DeviceM);
        Console.WriteLine($"[ForkSvc] [{_name}] 创建实例 IP={ip}:{port}");
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Console.WriteLine($"[ForkSvc] [{_name}] 连接中...");
        await _client.ConnectAsync(ct);
        Console.WriteLine($"[ForkSvc] [{_name}] 连接成功");
    }
    public Task DisconnectAsync() => _client.DisconnectAsync();
    public bool IsConnected => _client.IsConnected;

    // ═══════════════════════════════════════════════════════════════
    //  批量读取状态
    // ═══════════════════════════════════════════════════════════════

    /// <summary>一次性读取全部货叉状态（M900~M915），输出到控制台。</summary>
    public async Task<ForkStatus> ReadAllStatusAsync(CancellationToken ct = default)
    {
        var result = await _client.ReadAsync(Addr.ReadStartAddr, Addr.ReadWordCount, ct);
        ushort raw = (ushort)(result.IntValues.Length > 0 ? result.IntValues[0] : 0);

        var status = new ForkStatus
        {
            HasPlate          = (raw & (1 << 0)) != 0,  // M900
            Cyl1RetractLimit  = (raw & (1 << 1)) != 0,  // M901
            Cyl1ExtendLimit   = (raw & (1 << 2)) != 0,  // M902
            Cyl2RetractLimit  = (raw & (1 << 3)) != 0,  // M903
            Cyl2ExtendLimit   = (raw & (1 << 4)) != 0,  // M904
            Cyl3RetractLimit  = (raw & (1 << 5)) != 0,  // M905
            Cyl3ExtendInPlace = (raw & (1 << 6)) != 0,  // M906
            Cyl1RetractCtrl   = (raw & (1 << 11)) != 0, // M911
            Cyl1ExtendCtrl    = (raw & (1 << 12)) != 0, // M912
            Cyl2RetractCtrl   = (raw & (1 << 13)) != 0, // M913
            Cyl2ExtendCtrl    = (raw & (1 << 14)) != 0, // M914
            Cyl3RetractCtrl   = (raw & (1 << 15)) != 0, // M915
            RawValue          = raw,
        };

        Console.WriteLine($"[ForkSvc] [{_name}] 状态 M900~M915=0x{raw:X4} " +
            $"有版={status.HasPlate} 缸1={status.Cyl1State} 缸2={status.Cyl2State} 缸3={status.Cyl3State}");
        return status;
    }

    // ═══════════════════════════════════════════════════════════════
    //  写输出信号（读-改-写，保护其他 bit）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>写单个 M 输出位（读-改-写，避免覆盖其他bit）。</summary>
    private async Task WriteMBitAsync(int mAddr, bool value, CancellationToken ct)
    {
        var result = await _client.ReadAsync(Addr.ReadStartAddr, Addr.ReadWordCount, ct);
        int current = result.IntValues.Length > 0 ? result.IntValues[0] : 0;
        int bitOffset = mAddr - Addr.ReadStartAddr; // M911→offset11
        int next = value ? (current | (1 << bitOffset)) : (current & ~(1 << bitOffset));
        await _client.WriteAsync(Addr.ReadStartAddr, next, ct);
        Console.WriteLine($"[ForkSvc] [{_name}] 写 M{mAddr}={(value ? 1 : 0)} (word 0x{current:X4}→0x{next:X4})");
    }

    /// <summary>气缸1退回（M911=1）</summary>
    public Task Cyl1RetractAsync(CancellationToken ct = default) => WriteMBitAsync(Addr.M_Cyl1RetractCtrl, true, ct);
    /// <summary>气缸1伸出（M912=1）</summary>
    public Task Cyl1ExtendAsync(CancellationToken ct = default) => WriteMBitAsync(Addr.M_Cyl1ExtendCtrl, true, ct);
    /// <summary>气缸2退回（M913=1）</summary>
    public Task Cyl2RetractAsync(CancellationToken ct = default) => WriteMBitAsync(Addr.M_Cyl2RetractCtrl, true, ct);
    /// <summary>气缸2伸出（M914=1）</summary>
    public Task Cyl2ExtendAsync(CancellationToken ct = default) => WriteMBitAsync(Addr.M_Cyl2ExtendCtrl, true, ct);
    /// <summary>气缸3退回（M915=1）</summary>
    public Task Cyl3RetractAsync(CancellationToken ct = default) => WriteMBitAsync(Addr.M_Cyl3RetractCtrl, true, ct);
    /// <summary>气缸3伸出（M916=1）</summary>
    public Task Cyl3ExtendAsync(CancellationToken ct = default) => WriteMBitAsync(Addr.M_Cyl3ExtendCtrl, true, ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Console.WriteLine($"[ForkSvc] [{_name}] 释放资源");
        Task.Run(async () => await _client.DisconnectAsync()).GetAwaiter().GetResult();
    }
}

/// <summary>货叉状态快照（一次读取 M900~M915 的结果）。</summary>
public sealed class ForkStatus
{
    public bool HasPlate          { get; set; }  // M900
    public bool Cyl1RetractLimit  { get; set; }  // M901
    public bool Cyl1ExtendLimit   { get; set; }  // M902
    public bool Cyl2RetractLimit  { get; set; }  // M903
    public bool Cyl2ExtendLimit   { get; set; }  // M904
    public bool Cyl3RetractLimit  { get; set; }  // M905
    public bool Cyl3ExtendInPlace { get; set; }  // M906
    public bool Cyl1RetractCtrl   { get; set; }  // M911
    public bool Cyl1ExtendCtrl    { get; set; }  // M912
    public bool Cyl2RetractCtrl   { get; set; }  // M913
    public bool Cyl2ExtendCtrl    { get; set; }  // M914
    public bool Cyl3RetractCtrl   { get; set; }  // M915
    public ushort RawValue        { get; set; }

    public string Cyl1State => Cyl1ExtendLimit ? "伸出" : (Cyl1RetractLimit ? "退回" : "运动/未知");
    public string Cyl2State => Cyl2ExtendLimit ? "伸出" : (Cyl2RetractLimit ? "退回" : "运动/未知");
    public string Cyl3State => Cyl3ExtendInPlace ? "伸出" : (Cyl3RetractLimit ? "退回" : "运动/未知");
}
