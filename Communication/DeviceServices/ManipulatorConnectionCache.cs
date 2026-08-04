using System;
using System.Collections.Generic;
using System.Linq;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices;

/// <summary>
/// 小机械手连接缓存（机械手 1~3）。
/// <para>
/// 机械手的 PLC 寄存器地址与天车完全一致（汇川 PLC Modbus TCP，同一套 D5000~D5029 / D4500~D4518），
/// 因此复用 <see cref="CraneService"/> 通信，底层走同一个 <see cref="ModbusTcpClient"/>。
/// </para>
/// <para>查询数据库时附加 <c>TypeName == "机械手"</c> 过滤，避免与天车数据混淆。</para>
/// </summary>
public sealed class ManipulatorConnectionCache
{
    private readonly object _lock = new();
    private readonly MotionConfig? _motionConfig;
    private readonly Dictionary<int, (string Name, string Ip)> _ipMappings = new();
    private readonly Dictionary<int, CraneService> _services = new();

    public ManipulatorConnectionCache(MotionConfig? motionConfig = null)
    {
        _motionConfig = motionConfig;
    }

    /// <summary>
    /// 从数据库 machine 表装载 3 台机械手的 IP（过滤 TypeName="机械手"）。
    /// 数据库无记录时使用硬编码默认 IP。装载后断开并清除旧连接。
    /// </summary>
    public void LoadFromMachineRows(List<MachineManagementRowVm> machineRows)
    {
        lock (_lock)
        {
            Console.WriteLine("[ManipulatorCache] 开始从数据库装载3台机械手IP(type_name=机械手)...");

            var nextMappings = new Dictionary<int, (string Name, string Ip)>
            {
                [1] = ("机械手1", FindIp(machineRows, "机械手1", "192.168.2.85")),
                [2] = ("机械手2", FindIp(machineRows, "机械手2", "192.168.2.86")),
                [3] = ("机械手3", FindIp(machineRows, "机械手3", "192.168.2.87")),
            };

            bool mappingChanged = _ipMappings.Count != nextMappings.Count
                || nextMappings.Any(kv => !_ipMappings.TryGetValue(kv.Key, out var old)
                    || !string.Equals(old.Ip, kv.Value.Ip, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(old.Name, kv.Value.Name, StringComparison.OrdinalIgnoreCase));

            if (!mappingChanged)
            {
                Console.WriteLine("[ManipulatorCache] 机械手IP映射未变化, 保留现有共享连接");
                return;
            }

            _ipMappings.Clear();
            foreach (var kv in nextMappings) _ipMappings[kv.Key] = kv.Value;

            foreach (var svc in _services.Values)
                _ = svc.DisconnectAsync();
            _services.Clear();

            foreach (var kv in _ipMappings)
                Console.WriteLine($"[ManipulatorCache] 映射：{kv.Key}号 -> {kv.Value.Name} / {kv.Value.Ip}");
        }
    }

    /// <summary>获取指定机械手的名称和 IP，未映射时抛出异常。</summary>
    public (string Name, string Ip) GetManipulatorInfo(int no)
    {
        lock (_lock)
        {
            if (!_ipMappings.TryGetValue(no, out var info))
                throw new InvalidOperationException($"[ManipulatorCache] 未找到 {no} 号机械手映射");
            return info;
        }
    }

    /// <summary>获取或创建指定机械手的 <see cref="CraneService"/>（复用已有连接）。</summary>
    public CraneService GetOrCreateService(int no)
    {
        lock (_lock)
        {
            if (_services.TryGetValue(no, out var existing))
            {
                Console.WriteLine($"[ManipulatorCache] 复用缓存服务：{no}号机械手");
                return existing;
            }

            var info = GetManipulatorInfo(no);
            var service = new CraneService(info.Name, info.Ip,
                pressureStopNormalPositionToleranceProvider: _motionConfig == null
                    ? null
                    : () => _motionConfig.Safety.PressureStopNormalPositionToleranceMm);
            _services[no] = service;
            Console.WriteLine($"[ManipulatorCache] 新建服务：{no}号 {info.Name} {info.Ip}");
            return service;
        }
    }

    /// <summary>按 TypeName="机械手" 且 Name 匹配查找 IP，数据库查不到用 fallback 默认值。</summary>
    private static string FindIp(List<MachineManagementRowVm> rows, string name, string fallback)
    {
        var row = rows.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(x.TypeName) &&
            string.Equals(x.TypeName.Trim(), "机械手", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(x.Name) &&
            string.Equals(x.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

        if (row != null && !string.IsNullOrWhiteSpace(row.Ip))
        {
            Console.WriteLine($"[ManipulatorCache] [{name}] 数据库查到IP: {row.Ip.Trim()}");
            return row.Ip.Trim();
        }

        Console.WriteLine($"[ManipulatorCache] [{name}] 数据库未查到IP，使用默认值: {fallback}");
        return fallback;
    }
}
