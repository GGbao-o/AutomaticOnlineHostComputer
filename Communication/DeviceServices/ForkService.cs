using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using Addr = AutomaticOnlineHostComputer.Communication.DeviceAddresses.ForkAddress;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices;

/// <summary>
/// 货叉通信服务（三菱 MC 协议，M 寄存器，端口 9000）。
/// <para>
/// 新货叉协议: M900/M901/M902 为输入反馈，M911~M914 为中控动作命令。
/// 所有动作命令都按“清其它动作位→置目标动作位”写入，避免旧命令位残留叠加。
/// </para>
/// </summary>
public sealed class ForkService : IDisposable
{
    private readonly MitsubishiMcClient _client;
    private readonly string _name;
    private readonly object _statusLogLock = new();
    private bool _disposed;
    private ushort? _lastLoggedRaw;
    private DateTime _lastStatusLogAtUtc;
    private static readonly TimeSpan StatusLogHeartbeat = TimeSpan.FromSeconds(10);

    /// <param name="name">调试名称</param>
    /// <param name="ip">三菱 PLC IP</param>
    /// <param name="port">MC 协议端口，默认 9000</param>
    public ForkService(string name, string ip, int port = 9000)
    {
        _name = name;
        _client = new MitsubishiMcClient(ip, port, MitsubishiMcClient.DeviceM, frameType: MitsubishiMcClient.McFrameType.A1E) { UseBitReadForM = true };
        Console.WriteLine($"[ForkSvc] [{_name}] 创建实例 IP={ip}:{port} UseBitReadForM=true");
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

    /// <summary>一次性读取全部货叉状态（M900~M914），输出到控制台。</summary>
    public async Task<ForkStatus> ReadAllStatusAsync(CancellationToken ct = default)
    {
        var result = await _client.ReadAsync(Addr.ReadStartAddr, Addr.ReadWordCount, ct);
        ushort raw = (ushort)(result.IntValues.Length > 0 ? result.IntValues[0] : 0);

        var status = new ForkStatus
        {
            // M900~M902: 输入信号（货叉→中控）
            HasPlate      = (raw & (1 << 0)) != 0,  // M900 bit0
            AtStandbyPos  = (raw & (1 << 1)) != 0,  // M901 bit1
            AtPos3        = (raw & (1 << 2)) != 0,  // M902 bit2

            // M911~M914: 输出信号（中控→货叉）
            GoStandby        = (raw & (1 << 11)) != 0, // M911 bit11
            FeedToBoring     = (raw & (1 << 12)) != 0, // M912 bit12
            PickFromBoring   = (raw & (1 << 13)) != 0, // M913 bit13
            StandbyToPos3    = (raw & (1 << 14)) != 0, // M914 bit14

            RawValue      = raw,
        };

        // 前端主循环会在多个判断点读取同一货叉。仅在原始位变化或心跳周期到达时输出，
        // 保留状态变化的诊断价值，同时避免每500ms重复刷相同日志。
        if (ShouldLogStatus(raw))
        {
            Console.WriteLine($"[ForkSvc] [{_name}] 状态 M900~M914=0x{raw:X4} " +
                $"有版={status.HasPlate} 待机={status.AtStandbyPos} Pos3={status.AtPos3} " +
                $"命令[M911={status.GoStandby},M912={status.FeedToBoring},M913={status.PickFromBoring},M914={status.StandbyToPos3}]");
        }
        return status;
    }

    private bool ShouldLogStatus(ushort raw)
    {
        var now = DateTime.UtcNow;
        lock (_statusLogLock)
        {
            bool changed = _lastLoggedRaw != raw;
            bool heartbeat = now - _lastStatusLogAtUtc >= StatusLogHeartbeat;
            if (!changed && !heartbeat) return false;
            _lastLoggedRaw = raw;
            _lastStatusLogAtUtc = now;
            return true;
        }
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

    /// <summary>写一个货叉动作命令。M911~M914 同一时刻只保留一个动作位。</summary>
    private async Task WriteMotionCommandAsync(int commandAddr, string actionName, CancellationToken ct)
    {
        var result = await _client.ReadAsync(Addr.ReadStartAddr, Addr.ReadWordCount, ct);
        int current = result.IntValues.Length > 0 ? result.IntValues[0] : 0;
        int commandMask = 0;
        for (int m = Addr.M_GoStandby; m <= Addr.M_StandbyToPos3; m++)
            commandMask |= 1 << (m - Addr.ReadStartAddr);

        int next = (current & ~commandMask) | (1 << (commandAddr - Addr.ReadStartAddr));
        await _client.WriteAsync(Addr.ReadStartAddr, next, ct);
        Console.WriteLine($"[ForkSvc] [{_name}] {actionName}: M{commandAddr}=1 (清M911~M914, word 0x{current:X4}→0x{next:X4})");
    }

    /// <summary>货叉回待机位/原点（M911=1）。</summary>
    public Task GoStandbyAsync(CancellationToken ct = default) =>
        WriteMotionCommandAsync(Addr.M_GoStandby, "回待机位", ct);

    /// <summary>货叉去双头镗送料，并由PLC自动回待机位（M912=1）。</summary>
    public Task GoFeedToBoringAsync(CancellationToken ct = default) =>
        WriteMotionCommandAsync(Addr.M_FeedToBoring, "去双头镗送料并回待机", ct);

    /// <summary>货叉去双头镗取料（M913=1）。</summary>
    public Task GoPickFromBoringAsync(CancellationToken ct = default) =>
        WriteMotionCommandAsync(Addr.M_PickFromBoring, "去双头镗取料", ct);

    /// <summary>货叉从待机位直接送 Pos3 天车位（M914=1，跳过双头镗）。</summary>
    public Task GoStandbyToPos3Async(CancellationToken ct = default) =>
        WriteMotionCommandAsync(Addr.M_StandbyToPos3, "待机位直接送Pos3", ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Console.WriteLine($"[ForkSvc] [{_name}] 释放资源");
        Task.Run(async () => await _client.DisconnectAsync()).GetAwaiter().GetResult();
    }
}

/// <summary>货叉状态快照（一次读取 M900~M914 的结果）。</summary>
public sealed class ForkStatus
{
    // ── 输入信号（货叉→中控）─────────────────────────────────────
    /// <summary>M900  货叉有版信号</summary>
    public bool HasPlate      { get; set; }
    /// <summary>M901  货叉在待机位状态</summary>
    public bool AtStandbyPos  { get; set; }
    /// <summary>M902  货叉在 Pos3 天车取料位</summary>
    public bool AtPos3        { get; set; }
    // ── 输出信号（中控→货叉）─────────────────────────────────────
    /// <summary>M911  货叉回待机位控制</summary>
    public bool GoStandby        { get; set; }
    /// <summary>M912  去双头镗送料并回待机</summary>
    public bool FeedToBoring     { get; set; }
    /// <summary>M913  去双头镗取料</summary>
    public bool PickFromBoring   { get; set; }
    /// <summary>M914  待机位直接送 Pos3</summary>
    public bool StandbyToPos3    { get; set; }
    /// <summary>原始字值（调试用）</summary>
    public ushort RawValue    { get; set; }

    /// <summary>当前所在工位描述</summary>
    public string CurrentPosition =>
        AtStandbyPos ? "待机位" :
        AtPos3       ? "Pos3天车位" :
        "未知位";

    public string CommandText =>
        GoStandby ? "M911回待机" :
        FeedToBoring ? "M912送料" :
        PickFromBoring ? "M913取料" :
        StandbyToPos3 ? "M914直送Pos3" :
        "无命令";
}
