using System;
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
    /// <summary>绝对移动参数</summary>
    public AbsMoveSection AbsMove { get; set; } = new();
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
        public AxisSpeed X { get; set; } = new() { Speed = 150, Accel = 80, Decel = 80 };
        /// <summary>Y轴绝对速度</summary>
        public AxisSpeed Y { get; set; } = new() { Speed = 80, Accel = 60, Decel = 60 };
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

    /// <summary>研磨自动流程参数</summary>
    public GrindingSection Grinding { get; set; } = new();

    public sealed class GrindingSection
    {
        /// <summary>Z下降公式系数1：上料架取料 Z = 1506 - Round((d/2/zFactor1) + (d/2/zFactor2))</summary>
        public double ZFactor1 { get; set; } = 0.9537;
        /// <summary>Z下降公式系数2</summary>
        public double ZFactor2 { get; set; } = 0.866;
        /// <summary>Z轴安全高度(mm)，充磁/退磁后先升到此绝对Z坐标再水平移动（Z变小=向上）</summary>
        public int SafeZHeight { get; set; } = 400;
        /// <summary>研磨机天车编号(默认5号)</summary>
        public int CraneNo { get; set; } = 5;
        /// <summary>研磨机状态轮询间隔(ms)</summary>
        public int PollIntervalMs { get; set; } = 500;
        /// <summary>研磨机握手超时(ms)，等待请求上料/锁紧/松开等信号</summary>
        public int HandshakeTimeoutMs { get; set; } = 60_000;
    }

    /// <summary>斜床 Y 轴移动公式参数</summary>
    public SkewBedSection SkewBed { get; set; } = new();

    public sealed class SkewBedSection
    {
        /// <summary>各斜床顶尖距离 (站号→mm)，默认 1450</summary>
        public Dictionary<string, int> CenterDistances { get; set; } = new()
        {
            ["ST601"] = 1410, ["ST602"] = 1450, ["ST603"] = 1450, ["ST604"] = 1450, ["ST605"] = 1450,
            ["ST606"] = 1450, ["ST607"] = 1450, ["ST608"] = 1450, ["ST609"] = 1450, ["ST610"] = 1450,
        };
        /// <summary>大孔(堵孔100) Y轴移动距离，默认 125mm</summary>
        public int LargeBoreOffset { get; set; } = 117;
        /// <summary>小孔(堵孔70) Y轴移动距离，默认 65mm</summary>
        public int SmallBoreOffset { get; set; } = 55;
        /// <summary>机械手1 安全位置 Y 坐标(mm)，天车进入货叉区域前必须确认机械手在此位置</summary>
        public int Manipulator1SafeY { get; set; } = 800;

        /// <summary>根据站号和版孔类型计算 Y 轴目标偏移：Y = (顶尖距离-长度)/2 + 孔偏移</summary>
        public int ComputeYOffset(string stationCode, int workpieceLength, int plugHole)
        {
            int centerDist = CenterDistances.TryGetValue(stationCode, out var d) ? d : 1450;
            int boreOffset = plugHole == 100 ? LargeBoreOffset : SmallBoreOffset; // 100=大孔, 70=小孔
            return (centerDist - workpieceLength) / 2 + boreOffset;
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
}
