using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using Addr = AutomaticOnlineHostComputer.Communication.DeviceAddresses.CenteringRackAddress;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices;

/// <summary>
/// 机加居中上料架通信服务（三菱 MC 协议，端口 9000）。
/// <para>
/// 功能：工件居中定位 + 测量板长 + 通知上位机取料。
/// 天车取料完成后写 M801=1 通知上料架复位。
/// </para>
/// <para>M 区信号通过 M800 起始读 1 字（16bit）批量获取；D100 单独读字。</para>
/// </summary>
public sealed class CenteringRackService : IDisposable
{
    private MitsubishiMcClient? _client;
    private readonly McConnectionCache? _cache;
    private readonly string? _ip;
    private readonly int _port;
    private readonly string _name;
    private readonly bool _ownsClient;
    private readonly object _statusLogLock = new();
    private string? _lastStatusLogKey;
    private DateTime _lastStatusLogAtUtc;
    private static readonly TimeSpan StatusLogHeartbeat = TimeSpan.FromSeconds(10);
    private bool _lastPickupLoggedValue;
    private DateTime _lastPickupLogAtUtc;
    private int _lastPlateLengthLoggedValue;
    private DateTime _lastPlateLengthLogAtUtc;
    private bool _disposed;

    private MitsubishiMcClient Client => _client ?? throw new InvalidOperationException($"[CenteringRackSvc] [{_name}] 未连接MC客户端");

    /// <param name="name">调试名称（如"线体1居中上料架"）</param>
    /// <param name="ip">三菱 PLC IP 地址（待确认）</param>
    /// <param name="port">MC 协议端口，默认 9000</param>
    public CenteringRackService(string name, string ip, int port = 9000)
    {
        _name = name;
        _ip = ip;
        _port = port;
        _ownsClient = true;
        _client = new MitsubishiMcClient(ip, port, MitsubishiMcClient.DeviceM, frameType: MitsubishiMcClient.McFrameType.A1E);
        Console.WriteLine($"[CenteringRackSvc] [{_name}] 创建实例 IP={ip}:{port}");
    }

    /// <summary>使用共享MC连接(避免多引擎重复连同一PLC)</summary>
    public CenteringRackService(string name, MitsubishiMcClient sharedClient)
    {
        _name = name;
        _client = sharedClient;
        Console.WriteLine($"[CenteringRackSvc] [{_name}] 使用共享连接");
    }

    /// <summary>使用MC连接缓存托管连接；即使首连失败，后续重连也不会退化成多条独立TCP。</summary>
    public CenteringRackService(string name, McConnectionCache cache, string ip, int port = 9000)
    {
        _name = name;
        _cache = cache;
        _ip = ip;
        _port = port;
        Console.WriteLine($"[CenteringRackSvc] [{_name}] 使用MC缓存连接 {ip}:{port}");
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Console.WriteLine($"[CenteringRackSvc] [{_name}] 连接中...");
        if (_cache != null)
        {
            _client = await _cache.GetOrCreateAsync(_ip!, _port, ct).ConfigureAwait(false);
        }
        else
        {
            await Client.ConnectAsync(ct).ConfigureAwait(false);
        }
        Console.WriteLine($"[CenteringRackSvc] [{_name}] ✔ 连接成功");
    }
    public Task DisconnectAsync() => _client?.DisconnectAsync() ?? Task.CompletedTask;
    public bool IsConnected => _client?.IsConnected == true;

    // ═══════════════════════════════════════════════════════════════
    //  批量读取状态
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 一次性读取全部状态：M800~M816 位信号 + D100 板长数值。
    /// M800~M816 通过读 M800 起始 1 字（16bit）按位提取；
    /// D100 单独用 D 区读字。
    /// </summary>
    public async Task<CenteringRackStatus> ReadAllStatusAsync(CancellationToken ct = default)
    {
        // ── 读 M800 起始 2 字，覆盖 M800~M831 ──────────────────
        //    第1字 = M800~M815 (bit0~bit15)
        //    第2字 = M816~M831 (bit0~bit15，即 M816=第2字bit0)
        var result = await Client.ReadMAlignedWordAsync(Addr.M_ReadStartAddr, Addr.M_ReadWordCount, ct);
        ushort raw1 = (ushort)(result.IntValues.Length > 0 ? result.IntValues[0] : 0);
        ushort raw2 = (ushort)(result.IntValues.Length > 1 ? result.IntValues[1] : 0);

        var status = new CenteringRackStatus
        {
            // 第1字（M800~M815）
            RequestPickup    = (raw1 & (1 << 0))  != 0,  // M800 bit0
            PickupDone       = (raw1 & (1 << 1))  != 0,  // M801 bit1
            Station1HasPlate = (raw1 & (1 << 11)) != 0,  // M811 bit11
            Station2HasPlate = (raw1 & (1 << 12)) != 0,  // M812 bit12
            Station3HasPlate = (raw1 & (1 << 13)) != 0,  // M813 bit13
            Station4HasPlate = (raw1 & (1 << 14)) != 0,  // M814 bit14
            Station5HasPlate = (raw1 & (1 << 15)) != 0,  // M815 bit15
            // 第2字（M816~M831）
            Station6HasPlate = (raw2 & (1 << 0))  != 0,  // M816=第2字bit0
            RawMValue        = raw1,
            RawM816Word      = raw2,
        };

        // ── 读 D100 板长（D 区字寄存器）───────────────────────
        try
        {
            var dResult = await Client.ReadAsync(MitsubishiMcClient.DeviceD, Addr.D_PlateLength, 1, ct);
            status.PlateLength = (ushort)(dResult.IntValues.Length > 0 ? dResult.IntValues[0] : 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CenteringRackSvc] [{_name}] ⚠ 读取 D100 板长异常：{ex.Message}");
            status.PlateLength = 0;
        }

        if (ShouldLogStatus(raw1, raw2, status.PlateLength))
        {
            Console.WriteLine($"[CenteringRackSvc] [{_name}] 状态 M800~M831=0x{raw1:X4}|0x{raw2:X4} " +
                $"请求取料={status.RequestPickup} 板长={status.PlateLength}mm " +
                $"工位1={status.Station1HasPlate} 工位2={status.Station2HasPlate} " +
                $"工位3={status.Station3HasPlate} 工位4={status.Station4HasPlate} " +
                $"工位5={status.Station5HasPlate} 工位6={status.Station6HasPlate}");
        }
        return status;
    }

    private bool ShouldLogStatus(ushort raw1, ushort raw2, ushort plateLength)
    {
        string key = $"{raw1:X4}|{raw2:X4}|{plateLength}";
        var now = DateTime.UtcNow;
        lock (_statusLogLock)
        {
            bool changed = !string.Equals(_lastStatusLogKey, key, StringComparison.Ordinal);
            bool heartbeat = now - _lastStatusLogAtUtc >= StatusLogHeartbeat;
            if (!changed && !heartbeat) return false;
            _lastStatusLogKey = key;
            _lastStatusLogAtUtc = now;
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  读取单信号
    // ═══════════════════════════════════════════════════════════════

    /// <summary>单信号读取日志节流：值变化立即输出，否则每10秒心跳一次。</summary>
    private bool ShouldLogSingleSignal<T>(T value, ref T lastLogged, ref DateTime lastAtUtc) where T : struct
    {
        var now = DateTime.UtcNow;
        lock (_statusLogLock)
        {
            bool changed = !EqualityComparer<T>.Default.Equals(value, lastLogged);
            bool heartbeat = now - lastAtUtc >= StatusLogHeartbeat;
            if (!changed && !heartbeat) return false;
            lastLogged = value;
            lastAtUtc = now;
            return true;
        }
    }

    /// <summary>上料架是否请求取料（M800）</summary>
    public async Task<bool> IsRequestPickupAsync(CancellationToken ct = default)
    {
        var result = await Client.ReadMAlignedWordAsync(Addr.M_ReadStartAddr, 1, ct);
        bool val = result.IntValues.Length > 0 && (result.IntValues[0] & (1 << 0)) != 0;
        if (ShouldLogSingleSignal(val, ref _lastPickupLoggedValue, ref _lastPickupLogAtUtc))
            Console.WriteLine($"[CenteringRackSvc] [{_name}] M800 请求取料={(val ? 1 : 0)}");
        return val;
    }

    /// <summary>读单个M寄存器bit。FX系列按字读M区必须16点对齐, 这里由目标地址自动换算。</summary>
    public async Task<bool> ReadMBitAsync(int mAddr, int bitOffset, CancellationToken ct = default)
    {
        int alignedAddr = mAddr - (mAddr % 16);
        int alignedBitOffset = mAddr - alignedAddr;
        var result = await Client.ReadMAlignedWordAsync(alignedAddr, 1, ct);
        int val = result.IntValues.Length > 0 ? result.IntValues[0] : 0;
        return (val & (1 << alignedBitOffset)) != 0;
    }

    /// <summary>读M800起2字→返回第2字(M816~M831)</summary>
    public async Task<int> ReadM816Async(CancellationToken ct = default)
    {
        var result = await Client.ReadMAlignedWordAsync(800, 2, ct); // M800起2字=32bit
        return result.IntValues.Length > 1 ? result.IntValues[1] : 0;
    }

    /// <summary>读取居中测量的板长值（D100，单位 mm）</summary>
    public async Task<int> ReadPlateLengthAsync(CancellationToken ct = default)
    {
        var result = await Client.ReadAsync(MitsubishiMcClient.DeviceD, Addr.D_PlateLength, 1, ct);
        int len = result.IntValues.Length > 0 ? result.IntValues[0] : 0;
        if (ShouldLogSingleSignal(len, ref _lastPlateLengthLoggedValue, ref _lastPlateLengthLogAtUtc))
            Console.WriteLine($"[CenteringRackSvc] [{_name}] D100 板长={len}mm");
        return len;
    }

    // ═══════════════════════════════════════════════════════════════
    //  写输出信号
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 天车取料完成（M801=1→0 脉冲）。
    /// 天车取走工件后通知上料架复位，准备下一个工件。
    /// </summary>
    public async Task SetPickupDoneAsync(CancellationToken ct = default)
    {
        Console.WriteLine($"[CenteringRackSvc] [{_name}] ▶ 天车取料完成 M801=1");
        await Client.WriteMBitInWordAsync(Addr.M_ReadStartAddr, 1, true, ct);
        Console.WriteLine($"[CenteringRackSvc] [{_name}]   M801=1 已写入");

        // 短暂脉冲后复位
        await Task.Delay(500, ct);
        await Client.WriteMBitInWordAsync(Addr.M_ReadStartAddr, 1, false, ct);
        Console.WriteLine($"[CenteringRackSvc] [{_name}] ✔ M801=0 已复位");
    }

    // ═══════════════════════════════════════════════════════════════
    //  轮询等待
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 等待上料架请求取料（M800=1），超时抛 TimeoutException。
    /// 每 500ms 轮询一次。
    /// </summary>
    public async Task WaitForRequestPickupAsync(int timeoutMs = 60_000, CancellationToken ct = default)
    {
        Console.WriteLine($"[CenteringRackSvc] [{_name}] ⏳ 等待请求取料 M800=1（超时{timeoutMs / 1000}s）...");
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsRequestPickupAsync(ct))
            {
                Console.WriteLine($"[CenteringRackSvc] [{_name}] ✔ 请求取料 M800=1");
                return;
            }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException($"[CenteringRackSvc] [{_name}] 等待请求取料超时（{timeoutMs / 1000}s）");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Console.WriteLine($"[CenteringRackSvc] [{_name}] 释放资源");
        // 只有本服务自己创建的直连客户端才在Dispose里断开；共享/缓存连接由缓存或应用生命周期管理。
        if (_ownsClient && _client != null)
            Task.Run(async () => await _client.DisconnectAsync()).GetAwaiter().GetResult();
    }
}

/// <summary>机加居中上料架状态快照（一次读取 M800~M816 + D100 的结果）。</summary>
public sealed class CenteringRackStatus
{
    // ── M 区输入信号（上料架→中控）───────────────────────────────
    /// <summary>M800  上料架请求取料信号</summary>
    public bool RequestPickup    { get; set; }

    // ── M 区输出信号（中控→上料架）───────────────────────────────
    /// <summary>M801  天车取料完成信号</summary>
    public bool PickupDone       { get; set; }

    // ── 工位有版信号 ──────────────────────────────────────────
    /// <summary>M811  等待工位1有版</summary>
    public bool Station1HasPlate { get; set; }
    /// <summary>M812  等待工位2有版</summary>
    public bool Station2HasPlate { get; set; }
    /// <summary>M813  等待工位3有版</summary>
    public bool Station3HasPlate { get; set; }
    /// <summary>M814  等待工位4有版</summary>
    public bool Station4HasPlate { get; set; }
    /// <summary>M815  等待工位5有版</summary>
    public bool Station5HasPlate { get; set; }
    /// <summary>M816  等待工位6有版</summary>
    public bool Station6HasPlate { get; set; }

    // ── D 区 ───────────────────────────────────────────────
    /// <summary>D100  居中架测量板长（mm，16位无符号）</summary>
    public ushort PlateLength    { get; set; }

    /// <summary>M800~M815 原始字值（调试用）</summary>
    public ushort RawMValue      { get; set; }

    /// <summary>
    /// M816~M831 原始字值。与 <see cref="RawMValue"/> 来自同一次MC批量读取，
    /// 前端引擎应复用该值解析M817~M826，避免紧邻重复读取同一PLC字。
    /// </summary>
    public ushort RawM816Word { get; set; }

    /// <summary>当前有版的工位号列表（逗号分隔，调试用）</summary>
    public string ActiveStations
    {
        get
        {
            var list = new System.Collections.Generic.List<string>();
            if (Station1HasPlate) list.Add("1");
            if (Station2HasPlate) list.Add("2");
            if (Station3HasPlate) list.Add("3");
            if (Station4HasPlate) list.Add("4");
            if (Station5HasPlate) list.Add("5");
            if (Station6HasPlate) list.Add("6");
            return list.Count > 0 ? string.Join(",", list) : "无";
        }
    }

    public override string ToString()
        => $"请求取料={RequestPickup} 板长={PlateLength}mm 有版工位=[{ActiveStations}]";
}
