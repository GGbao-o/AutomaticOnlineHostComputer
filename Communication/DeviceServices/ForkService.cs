using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using Addr = AutomaticOnlineHostComputer.Communication.DeviceAddresses.ForkAddress;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices;

/// <summary>
/// 货叉通信服务（三菱 MC 协议，M 寄存器，端口 9000）。
/// <para>
/// 货叉由独立三菱 FX3G PLC 控制，4 个工位：待机位 / 1号位 / 2号位 / 3号位。
/// M900~M904 为输入信号（货叉→中控），M911~M914 为输出信号（中控→货叉）。
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
            // M900~M904: 输入信号（货叉→中控）
            HasPlate      = (raw & (1 << 0)) != 0,  // M900 bit0
            AtStandbyPos  = (raw & (1 << 1)) != 0,  // M901 bit1
            AtPos1        = (raw & (1 << 2)) != 0,  // M902 bit2
            AtPos2        = (raw & (1 << 3)) != 0,  // M903 bit3
            AtPos3        = (raw & (1 << 4)) != 0,  // M904 bit4

            // M911~M914: 输出信号（中控→货叉）
            GoStandby     = (raw & (1 << 11)) != 0, // M911 bit11
            GoPos1        = (raw & (1 << 12)) != 0, // M912 bit12
            GoPos2        = (raw & (1 << 13)) != 0, // M913 bit13
            GoPos3        = (raw & (1 << 14)) != 0, // M914 bit14

            RawValue      = raw,
        };

        Console.WriteLine($"[ForkSvc] [{_name}] 状态 M900~M915=0x{raw:X4} " +
            $"有版={status.HasPlate} 待机位={status.AtStandbyPos} 1号位={status.AtPos1} 2号位={status.AtPos2} 3号位={status.AtPos3}");
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

    /// <summary>货叉回待机位（M911=1）</summary>
    public Task GoStandbyAsync(CancellationToken ct = default) => WriteMBitAsync(Addr.M_GoStandby, true, ct);

    /// <summary>货叉去1号位（M912=1）</summary>
    public Task GoPos1Async(CancellationToken ct = default) => WriteMBitAsync(Addr.M_GoPos1, true, ct);

    /// <summary>货叉去2号位（M913=1）</summary>
    public Task GoPos2Async(CancellationToken ct = default) => WriteMBitAsync(Addr.M_GoPos2, true, ct);

    /// <summary>货叉去3号位（M914=1）</summary>
    public Task GoPos3Async(CancellationToken ct = default) => WriteMBitAsync(Addr.M_GoPos3, true, ct);

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
    // ── 输入信号（货叉→中控）─────────────────────────────────────
    /// <summary>M900  货叉有版信号</summary>
    public bool HasPlate      { get; set; }
    /// <summary>M901  货叉在待机位状态</summary>
    public bool AtStandbyPos  { get; set; }
    /// <summary>M902  货叉在1号位状态</summary>
    public bool AtPos1        { get; set; }
    /// <summary>M903  货叉在2号位状态</summary>
    public bool AtPos2        { get; set; }
    /// <summary>M904  货叉在3号位状态</summary>
    public bool AtPos3        { get; set; }

    // ── 输出信号（中控→货叉）─────────────────────────────────────
    /// <summary>M911  货叉回待机位控制</summary>
    public bool GoStandby     { get; set; }
    /// <summary>M912  货叉去1号位置控制</summary>
    public bool GoPos1        { get; set; }
    /// <summary>M913  货叉去2号位置控制</summary>
    public bool GoPos2        { get; set; }
    /// <summary>M914  货叉去3号位置控制</summary>
    public bool GoPos3        { get; set; }

    /// <summary>原始字值（调试用）</summary>
    public ushort RawValue    { get; set; }

    /// <summary>当前所在工位描述</summary>
    public string CurrentPosition =>
        AtStandbyPos ? "待机位" :
        AtPos1       ? "1号位" :
        AtPos2       ? "2号位" :
        AtPos3       ? "3号位" :
        "未知/运动中";
}
