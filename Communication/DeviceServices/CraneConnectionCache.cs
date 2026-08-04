using System;
using System.Collections.Generic;
using System.Linq;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices;

/// <summary>
/// 天车连接缓存。
/// <para>
/// 从数据库 machine 表加载 5 台天车的 IP → 按编号（1~5）缓存映射，
/// 并惰性创建/复用 <see cref="CraneService"/> 实例（每个天车一个长连接）。
/// </para>
/// <para>三台小机械手由独立的 <see cref="ManipulatorConnectionCache"/> 管理，虽共用同一套 PLC 寄存器地址。</para>
/// </summary>
public sealed class CraneConnectionCache
{
    private readonly object _lock = new();
    private readonly MotionConfig _motionConfig;

    private readonly Dictionary<int, (string Name, string Ip)> _ipMappings = new();
    private readonly Dictionary<int, CraneService> _services = new();
    private readonly Dictionary<int, DateTime> _lastReuseLogAtUtcByCrane = new();
    private static readonly TimeSpan ReuseLogHeartbeat = TimeSpan.FromSeconds(10);

    public CraneConnectionCache(MotionConfig motionConfig)
    {
        _motionConfig = motionConfig ?? throw new ArgumentNullException(nameof(motionConfig));
    }

    /// <summary>
    /// 从数据库 machine 表行装载天车 IP 映射（1~5 号）。
    /// 数据库无记录时使用硬编码默认 IP。装载后断开并清除旧连接。
    /// </summary>
    public void LoadFromMachineRows(List<MachineManagementRowVm> machineRows)
    {
        lock (_lock)
        {
            Console.WriteLine("[CraneCache] 开始从数据库装载5台天车IP...");

            var nextMappings = new Dictionary<int, (string Name, string Ip)>
            {
                [1] = ("1号线天车前", FindIp(machineRows, "1号线天车前", "192.168.2.81")),
                [2] = ("1号线天车后", FindIp(machineRows, "1号线天车后", "192.168.2.82")),
                [3] = ("2号线天车前", FindIp(machineRows, "2号线天车前", "192.168.2.83")),
                [4] = ("2号线天车后", FindIp(machineRows, "2号线天车后", "192.168.2.84")),
                [5] = ("研磨机天车", FindIp(machineRows, "研磨机天车", "192.168.2.80")),
            };

            bool mappingChanged = _ipMappings.Count != nextMappings.Count
                || nextMappings.Any(kv => !_ipMappings.TryGetValue(kv.Key, out var old)
                    || !string.Equals(old.Ip, kv.Value.Ip, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(old.Name, kv.Value.Name, StringComparison.OrdinalIgnoreCase));

            if (!mappingChanged)
            {
                Console.WriteLine("[CraneCache] 天车IP映射未变化, 保留现有共享连接");
                return;
            }

            _ipMappings.Clear();
            foreach (var kv in nextMappings) _ipMappings[kv.Key] = kv.Value;

            foreach (var kv in _services)
            {
                _ = kv.Value.DisconnectAsync();
            }
            _services.Clear();

            foreach (var kv in _ipMappings)
                Console.WriteLine($"[CraneCache] 映射：{kv.Key}号 -> {kv.Value.Name} / {kv.Value.Ip}");
        }
    }

    public (string Name, string Ip) GetCraneInfo(int craneNo)
    {
        lock (_lock)
        {
            if (!_ipMappings.TryGetValue(craneNo, out var info))
                throw new InvalidOperationException($"[CraneCache] 未找到 {craneNo} 号天车映射");

            return info;
        }
    }

    public CraneService GetOrCreateService(int craneNo)
    {
        lock (_lock)
        {
            if (_services.TryGetValue(craneNo, out var existing))
            {
                // 复用成功是高频热路径日志（前后端引擎每500ms调用），每台天车独立按10秒心跳节流。
                var now = DateTime.UtcNow;
                if (!_lastReuseLogAtUtcByCrane.TryGetValue(craneNo, out var lastAtUtc)
                    || now - lastAtUtc >= ReuseLogHeartbeat)
                {
                    _lastReuseLogAtUtcByCrane[craneNo] = now;
                    Console.WriteLine($"[CraneCache] 复用缓存服务：{craneNo}号");
                }
                return existing;
            }

            if (!_ipMappings.TryGetValue(craneNo, out var info))
                throw new InvalidOperationException($"[CraneCache] 未找到 {craneNo} 号天车映射，无法创建服务");

            var motion = _motionConfig.GetCraneSpeed(craneNo);
            var service = new CraneService(info.Name, info.Ip,
                xyTimeoutMs: motion.XyTimeoutMs, zTimeoutMs: motion.ZTimeoutMs,
                pressureStopNormalPositionToleranceProvider: () => _motionConfig.Safety.PressureStopNormalPositionToleranceMm);
            _services[craneNo] = service;
            Console.WriteLine($"[CraneCache] 新建服务：{craneNo}号 {info.Name} {info.Ip}");
            return service;
        }
    }

    public Dictionary<int, (string Name, string Ip)> GetMappingsSnapshot()
    {
        lock (_lock)
        {
            return _ipMappings.ToDictionary(x => x.Key, x => x.Value);
        }
    }

    /// <summary>
    /// 优先按 TypeName="天车" + Name 匹配 machine 表行取 IP。
    /// 旧数据库可能没有type_name；仅在完全不存在同名天车类型记录时，兼容退回Name-only匹配并告警。
    /// </summary>
    private static string FindIp(List<MachineManagementRowVm> machineRows, string name, string fallback)
    {
        var nameMatches = machineRows.Where(x =>
            !string.IsNullOrWhiteSpace(x.Name) &&
            string.Equals(x.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();

        var typedMatches = nameMatches.Where(x =>
            !string.IsNullOrWhiteSpace(x.TypeName) &&
            string.Equals(x.TypeName.Trim(), "天车", StringComparison.OrdinalIgnoreCase)).ToList();

        if (typedMatches.Count > 1)
            Console.WriteLine($"[CraneCache] ⚠ [{name}] 数据库存在{typedMatches.Count}条TypeName=天车的同名记录，使用第一条有效IP");

        if (typedMatches.Count > 0)
        {
            var typedRow = typedMatches.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Ip));
            if (typedRow != null)
            {
                var typedIp = typedRow.Ip.Trim();
                Console.WriteLine($"[CraneCache] [{name}] 按TypeName=天车精确匹配IP: {typedIp}");
                return typedIp;
            }

            Console.WriteLine($"[CraneCache] ⚠ [{name}] 已找到天车类型记录但IP为空，使用默认值: {fallback}");
            return fallback;
        }

        var legacyRow = nameMatches.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Ip));
        if (legacyRow != null)
        {
            var ip = legacyRow.Ip.Trim();
            Console.WriteLine($"[CraneCache] ⚠ [{name}] 未找到TypeName=天车记录，兼容按名称使用IP: {ip}；建议补全machine.type_name");
            return ip;
        }

        Console.WriteLine($"[CraneCache] [{name}] 数据库未查到IP，使用默认值: {fallback}");
        return fallback;
    }
}
