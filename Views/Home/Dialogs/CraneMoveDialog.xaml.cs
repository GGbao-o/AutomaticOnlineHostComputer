using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;

namespace AutomaticOnlineHostComputer.Views.Home.Dialogs;

public partial class CraneMoveDialog : Window
{
    private readonly CraneConnectionCache _craneCache;
    private readonly MotionConfig _cfg;
    private readonly Dictionary<string, MachineManagementRowVm> _stationCoords;
    private int _currentX, _currentY, _currentZ;

    /// <param name="craneCache">天车连接缓存(已注入)</param>
    /// <param name="cfg">运动参数配置(含速度/Z公式等)</param>
    /// <param name="stationCoords">工位坐标(站号→坐标+偏移)</param>
    public CraneMoveDialog(CraneConnectionCache craneCache, MotionConfig cfg,
        Dictionary<string, MachineManagementRowVm> stationCoords)
    {
        InitializeComponent();
        _craneCache = craneCache;
        _cfg = cfg;
        _stationCoords = stationCoords;

        // 天车列表
        CmbCrane.ItemsSource = CraneList;
        CmbCrane.DisplayMemberPath = "Name";
        CmbCrane.SelectedIndex = 0;
    }

    // ═══════════════════════════════════════════════════════════════
    //  天车定义 — 5台天车及各自可去的工位
    //
    //  ⚠ 注意事项:
    //    1. 暂不支持YZ轴移动, 只移动X轴
    //    2. 移动前必须确认天车Z已在安全高度, 否则低Z移动XY可能碰撞!
    //    3. 使用 CraneConnectionCache 共享连接, 不会新建TCP
    //    4. 速度从 motion_settings.json CtrnCraneSpeeds 段读取
    //    5. 天车校推偏移量从数据库 machine 表 x_dis 字段读取
    // ═══════════════════════════════════════════════════════════════

    private static readonly List<CraneDef> CraneList = new()
    {
        new(1, "ST901", "1号天车前", new[] {
            "ST711",  // 货叉1 Pos3位
            "ST713",  // 叉Pos3（天车取料位）
            "ST103",  // 1号线双头镗
            "ST107",  // 1号线打号机
            "ST105", "ST101", "ST106",  // 1号线中转架
        }),
        new(2, "ST102", "1号天车后", new[] {
            "ST105", "ST101", "ST106",  // 1号线中转架
            "ST108", "ST109", "ST111", "ST110", "ST112",  // 1号线斜床
            "ST019",  // 1号线动平衡下料架1
            "ST010",  // 研磨上料架1号位
        }),
        new(3, "ST104", "2号天车前", new[] {
            "ST712",  // 货叉2 Pos3位
            "ST402",  // 2号线双头镗
            "ST502",  // 2号线打号机
            "ST016", "ST017", "ST018",  // 2号线中转架
        }),
        new(4, "ST904", "2号天车后", new[] {
            "ST016", "ST017", "ST018",  // 2号线中转架
            "ST606", "ST607", "ST608", "ST609", "ST610",  // 2号线斜床
            "ST020",  // 2号线动平衡下料架1
            "ST021",  // 2号线不需动平衡位置
        }),
        new(5, "ST905", "研磨天车", new[] {
            "ST709",  // 研磨上料架
            "ST701", "ST702", "ST703", "ST704",  // 研磨机
            "ST710",  // 研磨下料架
        }),
    };

    private sealed class CraneDef
    {
        public int No { get; }
        public string StationCode { get; }
        public string Name { get; }
        public string[] TargetStations { get; }
        public CraneDef(int no, string code, string name, string[] stations)
        { No = no; StationCode = code; Name = name; TargetStations = stations; }
        public override string ToString() => Name;
    }

    // ═══════════════════════════════════════════════════════════════
    //  天车切换 → 刷新工位列表
    // ═══════════════════════════════════════════════════════════════

    private void CmbCrane_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbCrane.SelectedItem is not CraneDef def) return;

        // 工位列表: 站号 + 名称
        var stations = def.TargetStations
            .Select(code =>
            {
                string name = _stationCoords.TryGetValue(code, out var row) ? row.Name : "?";
                return new StationItem(code, $"{code} {name}");
            })
            .ToList();
        CmbStation.ItemsSource = stations;
        CmbStation.DisplayMemberPath = "Display";
        CmbStation.SelectedIndex = 0;

        TxtCurrent.Text = "点击「刷新位置」读取";
        TxtTarget.Text = "请先选择工位";
        BtnMove.IsEnabled = false;
    }

    private void CmbStation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTargetInfo();
    }

    private sealed class StationItem
    {
        public string Code { get; }
        public string Display { get; }
        public StationItem(string code, string display) { Code = code; Display = display; }
        public override string ToString() => Display;
    }

    // ═══════════════════════════════════════════════════════════════
    //  刷新当前位置 — 读取天车X/Y/Z/Busy状态
    //
    //  ⚠ 注意: _currentX只在点击此按钮时更新, 打开对话框前请先刷新
    //           未刷新时_currentX=0, 确认对话框会显示错误的ΔX
    // ═══════════════════════════════════════════════════════════════

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        BtnRefresh.IsEnabled = false;
        try
        {
            if (CmbCrane.SelectedItem is not CraneDef def) return;
            var crane = _craneCache.GetOrCreateService(def.No);
            if (!crane.IsConnected) await crane.ConnectAsync();

            using var cts = new CancellationTokenSource(5000);
            var s = await crane.ReadStatusAsync(cts.Token);
            if (s != null)
            {
                _currentX = s.XPos;
                _currentY = s.YPos;
                _currentZ = s.ZPos;
                TxtCurrent.Text = $"X={_currentX}  Y={_currentY}  Z={_currentZ}  {(s.Busy != 0 ? "⚠ 忙碌" : "✓ 空闲")}";
            }
        }
        catch (Exception ex) { TxtCurrent.Text = $"读取失败: {ex.Message}"; }
        finally { BtnRefresh.IsEnabled = true; UpdateTargetInfo(); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  更新目标信息
    // ═══════════════════════════════════════════════════════════════

    private void UpdateTargetInfo()
    {
        if (CmbCrane.SelectedItem is not CraneDef def || CmbStation.SelectedItem is not StationItem stn)
        {
            TxtTarget.Text = "请先选择天车和工位";
            BtnMove.IsEnabled = false;
            return;
        }

        if (!_stationCoords.TryGetValue(stn.Code, out var row))
        {
            TxtTarget.Text = $"工位 {stn.Code} 坐标未配置";
            BtnMove.IsEnabled = false;
            return;
        }

        // 天车偏移量
        int offsetX = 0;
        if (_stationCoords.TryGetValue(def.StationCode, out var craneRow))
            offsetX = (int)craneRow.XOffset;

        int stationX = (int)Math.Round(row.X);
        int targetX = stationX + offsetX;

        TxtTarget.Text = $"工位X={stationX} + 天车偏移={offsetX} = 目标X={targetX}\nYZ 保持不动";
        BtnMove.IsEnabled = true;
    }

    // ═══════════════════════════════════════════════════════════════
    //  确认移动 — YZ轴不动的X轴纯水平移动
    //
    //  ⚠ 重要安全注意事项:
    //    1. 仅移动X轴(Y-1 Z-1), 移动前必须确认Z已在安全高度!
    //       Z低时X移动可能碰撞沿线设备(打号机/中转架等)
    //    2. 无X11检查 — 天车磁铁上有工件时移动可能掉件
    //    3. 超时 240s(4分钟) — 适配最长X轴行程
    //    4. MoveAbsoluteAsync 内部有下压急停保护(PressureStopException)
    //    5. 速度设置来自配置文件 motion_settings.json craneSpeeds 段
    //    6. 共享区域(SafetyFlags)不检查 — 手动移动需操作工自己确认安全
    // ═══════════════════════════════════════════════════════════════

    private async void BtnMove_Click(object sender, RoutedEventArgs e)
    {
        if (CmbCrane.SelectedItem is not CraneDef def || CmbStation.SelectedItem is not StationItem stn)
            return;
        if (!_stationCoords.TryGetValue(stn.Code, out var row)) return;

        int offsetX = 0;
        if (_stationCoords.TryGetValue(def.StationCode, out var craneRow))
            offsetX = (int)craneRow.XOffset;

        int stationX = (int)Math.Round(row.X);
        int targetX = stationX + offsetX;

        // ═══ Z轴安全检查: 必须回原点后才能移动X, 防碰撞 ═══
        try
        {
            var crane = _craneCache.GetOrCreateService(def.No);
            if (!crane.IsConnected) await crane.ConnectAsync();
            using var checkCts = new CancellationTokenSource(5000);
            var pos = await crane.ReadStatusAsync(checkCts.Token);
            if (pos != null && Math.Abs(pos.ZPos) > 10) // Z未回原点(>10mm偏差)
            {
                MessageBox.Show(
                    $"⚠ 安全: Z轴未回原点!\n\n当前 Z={pos.ZPos}mm\n\n请先手动将天车 Z 轴回到原点(0)\n然后再执行 X 轴移动。\n\nZ轴不在最高点时移动X可能碰撞沿线设备。",
                    "安全提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _currentX = pos?.XPos ?? _currentX;
            _currentY = pos?.YPos ?? _currentY;
            _currentZ = pos?.ZPos ?? _currentZ;
            TxtCurrent.Text = pos != null
                ? $"X={pos.XPos}  Y={pos.YPos}  Z={pos.ZPos}  {(pos.Busy != 0 ? "⚠ 忙碌" : "✓ 空闲")}"
                : "读取失败";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"读取天车位置失败: {ex.Message}\n无法验证Z轴安全, 移动已取消。", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var msg = $"确认移动 {def.Name} 仅X轴？\n\n当前 X={_currentX}\n目标 X={targetX} ({stn.Display})\nΔX={targetX - _currentX}mm\nZ轴已回原点 ✓    YZ 保持不动";
        if (MessageBox.Show(msg, "确认移动", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        BtnMove.IsEnabled = false;
        try
        {
            var crane = _craneCache.GetOrCreateService(def.No);
            if (!crane.IsConnected) await crane.ConnectAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4)); // 240s超时
            var spd = _cfg.GetCraneSpeed(def.No);
            await crane.SetAbsSpeedAsync(spd.X.Speed, spd.X.Accel, spd.X.Decel,
                spd.Y.Speed, spd.Y.Accel, spd.Y.Decel,
                spd.Z.Speed, spd.Z.Accel, spd.Z.Decel, cts.Token);

            // 仅移动X, YZ传-1跳过
            await crane.MoveAbsoluteAsync(targetX, -1, -1, ct: cts.Token);

            var s = await crane.ReadStatusAsync(cts.Token);
            TxtCurrent.Text = s != null
                ? $"X={s.XPos}  Y={s.YPos}  Z={s.ZPos}  ✓ 到位"
                : "移动完成";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"移动失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { BtnMove.IsEnabled = true; }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
