using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AutomaticOnlineHostComputer.Infrastructure.Config;

/// <summary>
/// 运动参数配置文件加载器。
/// 从 Config/motion_settings.json 读取所有可调参数，
/// 文件不存在时使用内置默认值。
/// </summary>
public sealed class MotionConfig
{
    /// <summary>默认绝对移动参数（天车/机械手未单独配置时使用）</summary>
    public AbsMoveSection AbsMove { get; set; } = new();
    /// <summary>各天车独立速度(Key=天车编号1~5), 未配置则用AbsMove</summary>
    public Dictionary<int, AbsMoveSection> CraneSpeeds { get; set; } = new();
    /// <summary>各机械手独立速度(Key=机械手编号1~3), 未配置则用AbsMove</summary>
    public Dictionary<int, AbsMoveSection> ManipulatorSpeeds { get; set; } = new();

    /// <summary>取天车绝对移动参数(有独立配置用独立, 没有用默认)</summary>
    public AbsMoveSection GetCraneSpeed(int craneNo) => CraneSpeeds.TryGetValue(craneNo, out var s) ? s : AbsMove;
    /// <summary>取机械手绝对移动参数(有独立配置用独立, 没有用默认)</summary>
    public AbsMoveSection GetManipulatorSpeed(int manNo) => ManipulatorSpeeds.TryGetValue(manNo, out var s) ? s : AbsMove;

    /// <summary>相对移动（点动）参数</summary>
    public RelMoveSection RelMove { get; set; } = new();
    /// <summary>Z 轴安全策略</summary>
    public ZAxisSection ZAxis { get; set; } = new();
    /// <summary>安全检查参数</summary>
    public SafetySection Safety { get; set; } = new();
    /// <summary>抖动松料参数</summary>
    public ShakeSection Shake { get; set; } = new();
    /// <summary>流程引擎参数</summary>
    public EngineSection Engine { get; set; } = new();
    /// <summary>ERP下发任务文件导入参数。只负责导入到主页面任务列表, 不自动启动任务。</summary>
    public ErpTaskImportSection ErpTaskImport { get; set; } = new();
    /// <summary>斜床加工完成后导出给ERP/外部系统的完工文件参数。</summary>
    public SkewCompletionExportSection SkewCompletionExport { get; set; } = new();
    /// <summary>X绝对编码器下降前微调参数。只在Z下降取/放料前使用, 不改变原始运动路径。</summary>
    public XAbsFineTuneSection XAbsFineTune { get; set; } = new();

    /// <summary>单轴速度/加减速参数</summary>
    public sealed class AxisSpeed
    {
        public int Speed { get; set; } = 300;
        public int Accel { get; set; } = 150;
        public int Decel { get; set; } = 150;
    }

    public sealed class AbsMoveSection
    {
        public int Tolerance { get; set; } = 5;
        public int TimeoutMs { get; set; } = 240_000;
        public int PollIntervalMs { get; set; } = 500;
        /// <summary>X轴绝对速度</summary>
        public AxisSpeed X { get; set; } = new() { Speed = 200, Accel = 80, Decel = 80 };
        /// <summary>Y轴绝对速度</summary>
        public AxisSpeed Y { get; set; } = new() { Speed = 150, Accel = 60, Decel = 60 };
        /// <summary>Z轴绝对速度</summary>
        public AxisSpeed Z { get; set; } = new() { Speed = 50, Accel = 40, Decel = 40 };
    }

    public sealed class RelMoveSection
    {
        public int DefaultSpeed { get; set; } = 300;
        public int DefaultAccel { get; set; } = 150;
        public int DefaultDecel { get; set; } = 150;
    }

    public sealed class ZAxisSection
    {
        public int FastSpeed { get; set; } = 500;
        public int SlowSpeed { get; set; } = 100;
        public int SlowAccel { get; set; } = 80;
        public int SlowDecel { get; set; } = 80;
        public int StepDownDistance { get; set; } = 5;
        public int SafeZOffset { get; set; } = 50;
    }

    public sealed class SafetySection
    {
        public int MaxRetries { get; set; } = 3;
        public int MagnetOffMaxRetries { get; set; } = 50;
        public int MagnetOffRetryIntervalMs { get; set; } = 500;
    }

    public sealed class ShakeSection
    {
        public int DefaultCount { get; set; } = 5;
        public int DefaultAmplitude { get; set; } = 5;
        public int PauseMs { get; set; } = 200;
    }

    public sealed class EngineSection
    {
        public int QueuePollIntervalMs { get; set; } = 1000;
        public int StationWaitTimeoutMs { get; set; } = 30_000;
    }

    public sealed class ErpTaskImportSection
    {
        /// <summary>ERP下发任务文件路径。支持多行任务，按文件顺序导入。</summary>
        public string FilePath { get; set; } = @"D:\job1.txt";
        /// <summary>轮询间隔(ms)。现场文件写入很快, 但不需要高频占用UI线程。</summary>
        public int PollIntervalMs { get; set; } = 1000;
        /// <summary>解析失败时的原始内容备份目录。备份成功后才清空任务文件。</summary>
        public string ErrorDirectory { get; set; } = @"D:\ErpTaskError";
        /// <summary>是否打开主页面后自动开始监听。生产测试默认false, 由人工点击按钮启动。</summary>
        public bool EnabledOnStartup { get; set; } = false;
    }

    public sealed class SkewCompletionExportSection
    {
        /// <summary>斜床完工记录输出文件。多台斜床完成时按行追加, 不覆盖历史记录。</summary>
        public string FilePath { get; set; } = @"D:\job2.txt";
        /// <summary>总开关。false时不写job2, 但不影响斜床下料业务流程。</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>站号→ERP机器编码。后端引擎全程使用站号, 不再按IP二次推导。</summary>
        public Dictionary<string, string> StationMachineCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ST108"] = "SB10428",
            ["ST109"] = "SB10427",
            ["ST111"] = "SB10426",
            ["ST110"] = "SB10425",
            ["ST112"] = "SB10362",
            ["ST606"] = "SB10450",
            ["ST607"] = "SB10071",
            ["ST608"] = "SB10204",
            ["ST609"] = "SB10169",
            ["ST610"] = "SB10451",
        };

        public bool TryGetMachineCode(string stationCode, out string machineCode)
            => StationMachineCodes.TryGetValue(stationCode, out machineCode!);
    }

    public sealed class XAbsFineTuneSection
    {
        /// <summary>总开关。false时完全跳过X绝对编码器微调。</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>绝对编码器偏差允许值(mm)。|目标-当前|小于等于该值时不微调。</summary>
        public int ToleranceMm { get; set; } = 5;
        /// <summary>单次最大允许微调量(mm)。超过该值直接报警, 防止标定/坐标错误时大距离盲修。</summary>
        public int MaxAdjustMm { get; set; } = 50;
        /// <summary>每台天车每个工位的X绝对编码器标定值。值为-1表示该工位不做微调。</summary>
        public Dictionary<int, Dictionary<string, int>> StationTargets { get; set; } = new()
        {
            [3] = new()
            {
                ["ST714"] = -1, ["ST502"] = -1, ["ST016"] = -1, ["ST017"] = -1, ["ST018"] = -1
            },
            [4] = new()
            {
                ["ST016"] = -1, ["ST017"] = -1, ["ST018"] = -1,
                ["ST606"] = -1, ["ST607"] = -1, ["ST608"] = -1, ["ST609"] = -1, ["ST610"] = -1,
                ["ST020"] = -1, ["ST021"] = -1
            },
            [5] = new()
            {
                ["ST709"] = -1, ["ST710"] = -1, ["ST701"] = -1, ["ST702"] = -1, ["ST703"] = -1, ["ST704"] = -1
            }
        };

        public bool TryGetTarget(int craneNo, string stationCode, out int target)
        {
            target = -1;
            if (!StationTargets.TryGetValue(craneNo, out var stations)) return false;
            return stations.TryGetValue(stationCode, out target);
        }
    }

    /// <summary>研磨自动流程参数</summary>
    public GrindingSection Grinding { get; set; } = new();

    public sealed class GrindingSection
    {
        /// <summary>Z下降公式系数1：上料架取料 Z = 1506 - Round((d/2/zFactor1) + (d/2/zFactor2))</summary>
        public double ZFactor1 { get; set; } = 0.9537;
        /// <summary>Z下降公式系数2</summary>
        public double ZFactor2 { get; set; } = 0.866;
        /// <summary>Z轴安全高度(mm)，充磁/退磁后先升到此绝对Z坐标再水平移动（Z变小=向上）</summary>
        public int SafeZHeight { get; set; } = 0;
        /// <summary>研磨机天车编号(默认5号)</summary>
        public int CraneNo { get; set; } = 5;
        /// <summary>研磨机状态轮询间隔(ms)</summary>
        public int PollIntervalMs { get; set; } = 500;
        /// <summary>研磨机握手超时(ms)，等待请求上料/锁紧/松开等信号</summary>
        public int HandshakeTimeoutMs { get; set; } = 300_000;
        /// <summary>X11有版检测稳定延时(ms)，充磁后等待磁铁吸稳再读取</summary>
        public int X11StableDelayMs { get; set; } = 3000;
        /// <summary>PLC/CNC信号等待轮询间隔(ms)，降低延迟更快发现信号变化</summary>
        public int SignalPollIntervalMs { get; set; } = 50;
        /// <summary>研磨机状态卡死超时(ms)。Loading/Unloading/WaitingForUnload超过此值强制回Idle</summary>
        public int GrindingStuckTimeoutMs { get; set; } = 60_000;
    }

    /// <summary>平衡引擎安全参数</summary>
    public BalancingSection Balancing { get; set; } = new();

    public sealed class BalancingSection
    {
        /// <summary>后天车X坐标小于等于此值=已离开动平衡/研磨上料架区域, 机械手可安全进入</summary>
        public int RearCraneSafeX { get; set; } = -4000;
        /// <summary>机械手自定义YZ坐标(Key=M817/M818/M710/M700/M821/M720, 非0时覆盖数据库坐标)</summary>
        public Dictionary<string, BalancingArmCoord> ArmCoords { get; set; } = new();
    }

    /// <summary>机械手单位置自定义YZ(0=用数据库坐标)</summary>
    public sealed class BalancingArmCoord
    {
        public int Y { get; set; }
        public int Z { get; set; }
    }

    /// <summary>各天车归位X坐标(Key=天车编号1~5)。后天车同时作为机械手安全阈值。</summary>
    public Dictionary<int, int> CraneHomeX { get; set; } = new()
    {
        [1] = 1000, [2] = -5000, [3] = 1000, [4] = -5000, [5] = 0,
    };

    /// <summary>取天车归位X(有配置用配置, 没有用默认)</summary>
    public int GetCraneHomeX(int craneNo) => CraneHomeX.TryGetValue(craneNo, out var x) ? x : 1000;

    /// <summary>直径补偿(mm)：按站号写入CNC前叠加。不影响Z公式/UI显示/缓存直径。</summary>
    public Dictionary<string, double> DiameterOffsets { get; set; } = new();
    public double GetDiameterOffset(string stationCode)
        => DiameterOffsets.TryGetValue(stationCode, out var o) ? o : 0.0;

    /// <summary>斜床 Y 轴移动公式参数</summary>
    public SkewBedSection SkewBed { get; set; } = new();

    public sealed class SkewBedSection
    {
        private static readonly string[] Line1BedCodes = { "ST108", "ST109", "ST111", "ST110", "ST112" };
        private static readonly string[] Line2BedCodes = { "ST606", "ST607", "ST608", "ST609", "ST610" };

        /// <summary>各斜床顶尖距离 (站号→mm)，默认 1450</summary>
        public Dictionary<string, int> CenterDistances { get; set; } = new()
        {
            // 1号线斜床1~5 (DB站号: ST108~ST112)
            ["ST108"] = 1450, ["ST109"] = 1450, ["ST110"] = 1450, ["ST111"] = 1450, ["ST112"] = 1450,
            // 2号线斜床6~10 (DB站号: ST606~ST610)
            ["ST606"] = 1315, ["ST607"] = 1450, ["ST608"] = 1450, ["ST609"] = 1450, ["ST610"] = 1450,
        };

        /// <summary>
        /// 各斜床允许加工的最大工件长度(mm)。只用于任务分线和后端斜床匹配, 不参与Y轴偏移计算。
        /// 未配置或配置为0/负数时按不可加工处理, 防止长度能力未知时误派工件。
        /// </summary>
        public Dictionary<string, int> MaxWorkpieceLengthMm { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ST606"] = 1280, ["ST607"] = 1280, ["ST608"] = 1280, ["ST609"] = 1280, ["ST610"] = 1255,
            ["ST108"] = 1350, ["ST109"] = 1300, ["ST111"] = 1300, ["ST110"] = 1300, ["ST112"] = 1170,
        };

        /// <summary>大孔(堵孔100) Y轴移动距离，默认 125mm</summary>
        public int LargeBoreOffset { get; set; } = 122;
        /// <summary>小孔(堵孔70) Y轴移动距离，默认 65mm</summary>
        public int SmallBoreOffset { get; set; } = 55;
        /// <summary>机械手1 安全位 Y 坐标(mm)，机械手无X轴, 每次取完料Y回1000</summary>
        public int Manipulator1SafeY { get; set; } = 1000;
        /// <summary>机械手2 安全位 Y 坐标(mm)</summary>
        public int Manipulator2SafeY { get; set; } = 1000;
        /// <summary>机械手3 安全位 Y 坐标(mm)</summary>
        public int Manipulator3SafeY { get; set; } = 1000;
        /// <summary>
        /// 大直径工件强制分配1号线阈值(mm)。
        /// 机械手1给1号线货叉送料时会经过2号线货叉区域; 超过此直径不进入2号线, 避免大板对大板干涉。
        /// 配置为0或负数表示关闭该防碰撞分配规则。
        /// </summary>
        public int LargeDiameterLine1OnlyMm { get; set; } = 300;

        /// <summary>
        /// ST108/ST606 共享区释放前的后天车X退避距离(mm)。
        /// 目标X=当前X+配置值, 不叠加数据库偏移; 退避成功后才允许释放共享区锁。
        /// </summary>
        public Dictionary<string, int> SharedAreaRetreatX { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ST108"] = 2000,
            ["ST606"] = 1000,
        };

        /// <summary>获取共享区释放前的X退避距离(mm)。未配置时返回0, 表示不额外退避。</summary>
        public int GetSharedAreaRetreatX(string stationCode)
            => SharedAreaRetreatX.TryGetValue(stationCode, out var x) ? x : 0;

        /// <summary>
        /// 每台斜床的设备级对刀开关(Key=ST108/ST109/.../ST610)。
        /// true 时不改任务工艺本身, 只在后端写斜床加工参数时覆盖加工模式=6。
        /// </summary>
        public Dictionary<string, bool> ToolSettingBeds { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ST108"] = false, ["ST109"] = false, ["ST110"] = false, ["ST111"] = false, ["ST112"] = false,
            ["ST606"] = false, ["ST607"] = false, ["ST608"] = false, ["ST609"] = false, ["ST610"] = false,
        };

        /// <summary>判断指定斜床是否启用设备级对刀模式。</summary>
        public bool IsToolSettingEnabled(string stationCode)
            => ToolSettingBeds.TryGetValue(stationCode, out var enabled) && enabled;

        /// <summary>获取指定斜床最大加工长度(mm)。未配置时返回0, 表示该斜床不参与长度匹配。</summary>
        public int GetMaxWorkpieceLengthMm(string stationCode)
            => MaxWorkpieceLengthMm.TryGetValue(stationCode, out var max) ? max : 0;

        /// <summary>判断指定斜床能否加工该长度。长度必须为正数且不超过该斜床配置上限。</summary>
        public bool CanProcessLength(string stationCode, double workpieceLength)
        {
            int max = GetMaxWorkpieceLengthMm(stationCode);
            return workpieceLength > 0 && max > 0 && workpieceLength <= max;
        }

        /// <summary>按线路返回斜床站号。返回空数组表示无效线路。</summary>
        public static IReadOnlyList<string> GetBedCodesForLine(int line) => line switch
        {
            1 => Line1BedCodes,
            2 => Line2BedCodes,
            _ => Array.Empty<string>()
        };

        /// <summary>判断某条线是否至少有一台斜床能加工该长度。</summary>
        public bool AnyBedCanProcessLine(int line, double workpieceLength)
        {
            foreach (var code in GetBedCodesForLine(line))
            {
                if (CanProcessLength(code, workpieceLength))
                    return true;
            }
            return false;
        }

        /// <summary>根据站号和版孔类型计算 Y 轴目标偏移：Y = (顶尖距离-长度)/2 + 孔偏移</summary>
        public int ComputeYOffset(string stationCode, double workpieceLength, int plugHole)
        {
            int centerDist = CenterDistances.TryGetValue(stationCode, out var d) ? d : 1450;
            int boreOffset = plugHole == 100 ? LargeBoreOffset : SmallBoreOffset; // 122=大孔, 55=小孔
            return (int)((centerDist - workpieceLength) / 2 + boreOffset);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  加载
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 从 Config/motion_settings.json 加载配置，文件不存在返回默认值。
    /// </summary>
    public static MotionConfig Load()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var path = Path.Combine(baseDir, "Config", "motion_settings.json");

        if (!File.Exists(path))
        {
            Console.WriteLine("[MotionConfig] 配置文件不存在，使用默认值。");
            Console.WriteLine($"[MotionConfig]   期望路径：{path}");
            return new MotionConfig();
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<MotionConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            Console.WriteLine($"[MotionConfig] 配置加载成功：{path}");
            Console.WriteLine($"[MotionConfig]   AbsSpeed X={config!.AbsMove.X.Speed} Y={config.AbsMove.Y.Speed} Z={config.AbsMove.Z.Speed}");
            Console.WriteLine($"[MotionConfig]   ZAxis Fast={config.ZAxis.FastSpeed} Slow={config.ZAxis.SlowSpeed}");
            return config;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MotionConfig] 配置加载失败：{ex.Message}，使用默认值。");
            return new MotionConfig();
        }
    }

    /// <summary>
    /// 保存到当前程序运行目录的 Config/motion_settings.json。
    /// 运动参数页修改设备级配置后立即调用, 引擎持有同一个 MotionConfig 实例可即时生效。
    /// </summary>
    public void Save()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var dir = Path.Combine(baseDir, "Config");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "motion_settings.json");
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(path, json);
        Console.WriteLine($"[MotionConfig] 配置已保存：{path}");
    }
}
