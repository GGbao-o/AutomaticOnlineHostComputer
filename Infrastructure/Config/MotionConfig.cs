using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
    public AxisAbsFineTuneSection XAbsFineTune { get; set; } = new();
    /// <summary>Y绝对编码器下降前微调参数。仅适用于1～5号天车，默认关闭，完成现场标定后才可启用。</summary>
    public AxisAbsFineTuneSection YAbsFineTune { get; set; } = new() { Enabled = false };
    /// <summary>所有天车共用的绝对编码器微调验证节奏。</summary>
    public AbsFineTuneVerificationSection AbsFineTuneVerification { get; set; } = new();

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
        /// <summary>天车仅移动X/Y时、以及复合动作XY阶段的最长等待时间。</summary>
        public int XyTimeoutMs { get; set; } = 120_000;
        /// <summary>天车仅移动Z时、以及复合动作Z阶段的最长等待时间。</summary>
        public int ZTimeoutMs { get; set; } = 120_000;
        /// <summary>兼容旧调用方的统一超时值；新天车流程改用XyTimeoutMs/ZTimeoutMs。</summary>
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
        /// <summary>
        /// Z 下降时 X2 下压触发后，停止位置与目标 Z 的最大允许差值。
        /// 在此范围内视为正常接触到位，恢复伺服后继续充/退磁等后续步骤；超出范围才暂停人工确认。
        /// </summary>
        public int PressureStopNormalPositionToleranceMm { get; set; } = 15;
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

    public sealed class AxisAbsFineTuneSection
    {
        /// <summary>总开关。false时完全跳过本轴绝对编码器微调。</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>绝对编码器偏差允许值(mm)。|目标-当前|小于等于该值时不微调。</summary>
        public int ToleranceMm { get; set; } = 5;
        /// <summary>单次最大允许微调量(mm)。超过该值直接报警, 防止标定/坐标错误时大距离盲修。</summary>
        public int MaxAdjustMm { get; set; } = 50;
        /// <summary>
        /// 每台天车的“绝对编码器变化量 / 显示坐标变化量”方向，只允许 -1 或 +1。
        /// 仅 Y 轴使用；未配置时保留历史兼容值 -1。
        /// </summary>
        public Dictionary<int, int> AbsolutePerDisplayDirections { get; set; } = new();
        /// <summary>每台天车每个工位的本轴绝对编码器标定值。值为-1表示该工位不做微调。</summary>
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

        public int GetAbsolutePerDisplayDirection(int craneNo, int fallbackDirection = -1)
        {
            int direction = AbsolutePerDisplayDirections.TryGetValue(craneNo, out int configured)
                ? configured
                : fallbackDirection;
            if (direction is not (-1 or +1))
            {
                throw new InvalidDataException(
                    $"天车#{craneNo}绝对编码器/显示坐标方向只能配置为-1或+1，当前为{direction}");
            }

            return direction;
        }
    }

    /// <summary>
    /// XY 绝对编码器微调前后，读取 PLC/编码器状态的共享验证节奏。
    /// 配置页的修改会即时写入此对象；每次微调开始时会取一次经校验的快照。
    /// </summary>
    public sealed class AbsFineTuneVerificationSection
    {
        /// <summary>XY 到位后、首次读取稳定窗口前的等待时间。</summary>
        public int BeforeReadSettleDelayMs { get; set; } = 1200;
        /// <summary>稳定窗口内要求的连续采样次数。</summary>
        public int StableSampleCount { get; set; } = 3;
        /// <summary>稳定窗口的采样间隔。</summary>
        public int StableSampleIntervalMs { get; set; } = 200;
        /// <summary>稳定窗口中显示坐标与绝对编码器各自允许的最大范围。</summary>
        public int StableRangeMm { get; set; } = 1;
        /// <summary>微调动作完成后的最短等待时间。</summary>
        public int AfterMoveMinSettleDelayMs { get; set; } = 1200;

        /// <summary>将手工 JSON 中的非法值回退为现场确认的安全默认值。</summary>
        public FineTuneVerificationValues GetValidated()
        {
            int beforeReadSettle = BeforeReadSettleDelayMs is >= 100 and <= 10_000
                ? BeforeReadSettleDelayMs : 1200;
            int stableSampleCount = StableSampleCount is >= 2 and <= 20
                ? StableSampleCount : 3;
            int stableSampleInterval = StableSampleIntervalMs is >= 50 and <= 2_000
                ? StableSampleIntervalMs : 200;
            int stableRange = StableRangeMm is >= 0 and <= 20
                ? StableRangeMm : 1;
            int afterMoveMinSettle = AfterMoveMinSettleDelayMs is >= 100 and <= 2_000
                ? AfterMoveMinSettleDelayMs : 1200;

            return new FineTuneVerificationValues(
                beforeReadSettle, stableSampleCount, stableSampleInterval, stableRange, afterMoveMinSettle);
        }
    }

    /// <summary>单次微调生命周期内固定使用的验证节奏快照。</summary>
    public readonly record struct FineTuneVerificationValues(
        int BeforeReadSettleDelayMs,
        int StableSampleCount,
        int StableSampleIntervalMs,
        int StableRangeMm,
        int AfterMoveMinSettleDelayMs);

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
        /// <summary>PLC/CNC信号等待轮询间隔(ms)。200ms对秒级信号变化足够，降低Modbus轮询压力</summary>
        public int SignalPollIntervalMs { get; set; } = 200;
        /// <summary>研磨天车上料中观察时间(分钟)。超时后暂停引擎并保留状态/缓存，等待人工确认。</summary>
        public int LoadingTimeoutMinutes { get; set; } = 10;
        /// <summary>研磨机加工完成后等待天车下料的观察时间(分钟)。</summary>
        public int WaitingForUnloadTimeoutMinutes { get; set; } = 20;
        /// <summary>研磨天车下料中观察时间(分钟)。超时后暂停引擎并保留状态/缓存，等待人工确认。</summary>
        public int UnloadingTimeoutMinutes { get; set; } = 10;
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
        /// <summary>
        /// 机械手1旧版单安全位配置。仅为兼容旧motion_settings.json保留；
        /// 1/2号线正常流程改用下面两个分线路安全位，避免共用一个远端坐标造成无效长距离返回。
        /// </summary>
        public int Manipulator1SafeY { get; set; } = 11500;
        /// <summary>机械手1给1号线货叉ST711放板后，首次离开叉区并允许货叉启动的安全Y。</summary>
        public int Manipulator1Line1SafeY { get; set; } = 11500;
        /// <summary>机械手1给2号线货叉ST712放板后，首次离开叉区并允许货叉启动的安全Y。</summary>
        public int Manipulator1Line2SafeY { get; set; } = 1000;
        /// <summary>
        /// 机械手1完成货叉放行后，为下一次从总上料架取板而继续前往的待机Y。
        /// 该位置与“货叉释放安全Y”职责不同，不能通过复用某条线的安全点隐式表达。
        /// </summary>
        public int Manipulator1PickupStandbyY { get; set; } = 1000;
        /// <summary>机械手1安全位判断容差(mm)。保持原生产逻辑的±10mm。</summary>
        public const int Manipulator1SafeYTolerance = 10;

        /// <summary>取得机械手1给指定线路放板后必须到达的安全Y。</summary>
        public int GetManipulator1SafeYForLine(int lineNo) => lineNo switch
        {
            1 => Manipulator1Line1SafeY,
            2 => Manipulator1Line2SafeY,
            _ => throw new ArgumentOutOfRangeException(nameof(lineNo), lineNo, "机械手1只服务1号线和2号线")
        };

        /// <summary>
        /// 前天车/主页面使用的全局安全判断。
        /// 现场已确认Y=1000和Y=11500对两条前天车都安全，因此到达任意一个配置点都可视为离开危险区。
        /// 本线货叉首次放行仍使用GetManipulator1SafeYForLine校验本线路目标；
        /// 放行后机械手可继续前往Manipulator1PickupStandbyY，不再阻塞已启动的货叉状态机。
        /// </summary>
        public bool IsManipulator1AtAnySafeY(int currentY)
            => Math.Abs(currentY - Manipulator1Line1SafeY) <= Manipulator1SafeYTolerance
               || Math.Abs(currentY - Manipulator1Line2SafeY) <= Manipulator1SafeYTolerance;
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
            Console.WriteLine($"[MotionConfig]   机械手1货叉释放安全Y: 1号线={config.SkewBed.Manipulator1Line1SafeY}, 2号线={config.SkewBed.Manipulator1Line2SafeY}, 取板待机Y={config.SkewBed.Manipulator1PickupStandbyY}, 容差=±{SkewBedSection.Manipulator1SafeYTolerance}");
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
        var tempPath = path + ".tmp";
        var backupPath = path + ".bak";
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        try
        {
            // 临时文件与正式文件放在同一目录，后续替换不会跨磁盘。
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // 替换前先验证临时文件可完整反序列化，避免把截断JSON变成正式配置。
            var verifyJson = File.ReadAllText(tempPath);
            _ = JsonSerializer.Deserialize<MotionConfig>(verifyJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidDataException("运动配置校验失败：反序列化结果为空");

            if (File.Exists(path))
                File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, path);

            Console.WriteLine($"[MotionConfig] 配置已原子保存：{path}" +
                              (File.Exists(backupPath) ? $"（备份：{backupPath}）" : string.Empty));
        }
        finally
        {
            // 保存失败时保留原正式文件，只清理未完成的临时文件。
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) { Console.WriteLine($"[MotionConfig] 临时文件清理失败：{ex.Message}"); }
            }
        }
    }
}
