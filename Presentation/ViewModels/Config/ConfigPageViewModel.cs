using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Infrastructure.Config;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Config;

/// <summary>
/// 配置页面 ViewModel — 所有修改直接写入共享 MotionConfig 实例，引擎即时生效。
/// 保存按钮调 _cfg.Save() 持久化到 JSON，无需重新编译。
/// </summary>
public sealed class ConfigPageViewModel : INotifyPropertyChanged
{
    private readonly MotionConfig _cfg;
    private readonly Func<int, Task<CraneService?>>? _craneProvider;

    public ConfigPageViewModel(MotionConfig cfg, Func<int, Task<CraneService?>>? craneProvider = null)
    {
        _cfg = cfg;
        _craneProvider = craneProvider;
        SaveCommand = new RelayCommand(_ => Save());
        ReadCurrentXCommand = new RelayCommand(async p => await ReadCurrentEncoderAsync(p as string, "X"));
        ReadCurrentYCommand = new RelayCommand(async p => await ReadCurrentEncoderAsync(p as string, "Y"));
        RefreshTabs();
    }

    public ICommand SaveCommand { get; }
    public ICommand ReadCurrentXCommand { get; }
    public ICommand ReadCurrentYCommand { get; }

    // ═══════════════════════════════════════════════════════════════
    //  Tab1 — 天车速度（直接读写 _cfg.CraneSpeeds 内 AxisSpeed 对象）
    // ═══════════════════════════════════════════════════════════════
    public ObservableCollection<CraneSpeedRow> CraneSpeedRows { get; } = new();

    // ═══════════════════════════════════════════════════════════════
    //  Tab2 — 机械手速度
    // ═══════════════════════════════════════════════════════════════
    public ObservableCollection<ManipulatorSpeedRow> ManipulatorSpeedRows { get; } = new();

    // ═══════════════════════════════════════════════════════════════
    //  Tab3 — 斜床参数
    // ═══════════════════════════════════════════════════════════════
    public ObservableCollection<SkewBedParamRow> SkewBedRows { get; } = new();
    /// <summary>四台研磨机最大加工长度(mm)，直接引用配置字典。</summary>
    public ObservableCollection<GrindingLengthRow> GrindingLengthRows { get; } = new();

    // ═══════════════════════════════════════════════════════════════
    //  Tab4 — 机械手坐标
    // ═══════════════════════════════════════════════════════════════
    public ObservableCollection<ArmCoordRow> ArmCoordRows { get; } = new();

    // ═══════════════════════════════════════════════════════════════
    //  Tab5 — 绝对值编码器标定
    // ═══════════════════════════════════════════════════════════════
    public ObservableCollection<int> CraneNumbers { get; } = new() { 1, 2, 3, 4, 5 };

    private int _selectedCraneNo = 1;
    public int SelectedCraneNo
    {
        get => _selectedCraneNo;
        set { _selectedCraneNo = value; RefreshStationTargetRows(); OnPropertyChanged(); }
    }

    public bool XAbsFineTuneEnabled
    {
        get => _cfg.XAbsFineTune.Enabled;
        set { _cfg.XAbsFineTune.Enabled = value; OnPropertyChanged(); }
    }

    public bool YAbsFineTuneEnabled
    {
        get => _cfg.YAbsFineTune.Enabled;
        set { _cfg.YAbsFineTune.Enabled = value; OnPropertyChanged(); }
    }

    public ObservableCollection<StationTargetRow> StationTargetRows { get; } = new();

    // ═══════════════════════════════════════════════════════════════
    //  Tab6 — 其他（直接读写 _cfg 属性）
    // ═══════════════════════════════════════════════════════════════

    // -- 直径补偿（发给双头镗/斜床CNC的直径 = ERP原始直径 + 此偏移）--
    public ObservableCollection<DiameterOffsetRow> DiameterOffsetRows { get; } = new();

    public int Crane1HomeX { get => GetHomeX(1); set => SetHomeX(1, value); }
    public int Crane2HomeX { get => GetHomeX(2); set => SetHomeX(2, value); }
    public int Crane3HomeX { get => GetHomeX(3); set => SetHomeX(3, value); }
    public int Crane4HomeX { get => GetHomeX(4); set => SetHomeX(4, value); }
    public int Crane5HomeX { get => GetHomeX(5); set => SetHomeX(5, value); }

    // 机械手1是两线共用设备。分线路配置表示“首次离开货叉区并允许货叉启动”的安全点；
    // 取板待机Y表示货叉放行后继续前往的预定位点，两者不能混为一个配置。
    public int Manipulator1Line1SafeY { get => _cfg.SkewBed.Manipulator1Line1SafeY; set { _cfg.SkewBed.Manipulator1Line1SafeY = value; OnPropertyChanged(); } }
    public int Manipulator1Line2SafeY { get => _cfg.SkewBed.Manipulator1Line2SafeY; set { _cfg.SkewBed.Manipulator1Line2SafeY = value; OnPropertyChanged(); } }
    public int Manipulator1PickupStandbyY { get => _cfg.SkewBed.Manipulator1PickupStandbyY; set { _cfg.SkewBed.Manipulator1PickupStandbyY = value; OnPropertyChanged(); } }
    public int Manipulator2SafeY { get => _cfg.SkewBed.Manipulator2SafeY; set { _cfg.SkewBed.Manipulator2SafeY = value; OnPropertyChanged(); } }
    public int Manipulator3SafeY { get => _cfg.SkewBed.Manipulator3SafeY; set { _cfg.SkewBed.Manipulator3SafeY = value; OnPropertyChanged(); } }

    public int RearCraneSafeX { get => _cfg.Balancing.RearCraneSafeX; set { _cfg.Balancing.RearCraneSafeX = value; OnPropertyChanged(); } }
    public int M2ReleaseLocksBelowY { get => _cfg.Balancing.M2ReleaseLocksBelowY; set { _cfg.Balancing.M2ReleaseLocksBelowY = value; OnPropertyChanged(); } }
    public int M3ReleaseLocksBelowY { get => _cfg.Balancing.M3ReleaseLocksBelowY; set { _cfg.Balancing.M3ReleaseLocksBelowY = value; OnPropertyChanged(); } }

    public int GrindingSafeZ { get => _cfg.Grinding.SafeZHeight; set { _cfg.Grinding.SafeZHeight = value; OnPropertyChanged(); } }
    public int GrindingCraneNo { get => _cfg.Grinding.CraneNo; set { _cfg.Grinding.CraneNo = value; OnPropertyChanged(); } }
    public string GrindingWorkpieceDisplayIp
    {
        get => _cfg.Grinding.WorkpieceDisplayIp;
        set { _cfg.Grinding.WorkpieceDisplayIp = value; OnPropertyChanged(); }
    }
    /// <summary>全线自动取料共用：充磁成功返回后，等待该时长再读取 X11 有板反馈（单位：ms）。</summary>
    public int X11StableDelayMs { get => _cfg.Grinding.X11StableDelayMs; set { _cfg.Grinding.X11StableDelayMs = value; OnPropertyChanged(); } }
    /// <summary>ST709取料后：先启动天车5 Z回安全高度，再等待该时长写M731释放传送带（单位：ms）。</summary>
    public int M731NotifyDelayMs { get => _cfg.Grinding.M731NotifyDelayMs; set { _cfg.Grinding.M731NotifyDelayMs = value; OnPropertyChanged(); } }
    /// <summary>ST010放料后：先启动1号线后天车Z回安全高度，再等待该时长写M721通知传送带（单位：ms）。</summary>
    public int M721NotifyDelayMs { get => _cfg.Grinding.M721NotifyDelayMs; set { _cfg.Grinding.M721NotifyDelayMs = value; OnPropertyChanged(); } }
    /// <summary>研磨上料FIFO允许的最大缓存笔数；超限时天车5在派发前暂停并等待人工确认。</summary>
    public int GrindingMaxFifoCount { get => _cfg.Grinding.MaxFifoCount; set { _cfg.Grinding.MaxFifoCount = value; OnPropertyChanged(); } }
    public int GrindingLoadingTimeoutMinutes { get => _cfg.Grinding.LoadingTimeoutMinutes; set { _cfg.Grinding.LoadingTimeoutMinutes = value; OnPropertyChanged(); } }
    public int GrindingWaitingForUnloadTimeoutMinutes { get => _cfg.Grinding.WaitingForUnloadTimeoutMinutes; set { _cfg.Grinding.WaitingForUnloadTimeoutMinutes = value; OnPropertyChanged(); } }
    public int GrindingUnloadingTimeoutMinutes { get => _cfg.Grinding.UnloadingTimeoutMinutes; set { _cfg.Grinding.UnloadingTimeoutMinutes = value; OnPropertyChanged(); } }

    public int LargeBoreOffset { get => _cfg.SkewBed.LargeBoreOffset; set { _cfg.SkewBed.LargeBoreOffset = value; OnPropertyChanged(); } }
    public int SmallBoreOffset { get => _cfg.SkewBed.SmallBoreOffset; set { _cfg.SkewBed.SmallBoreOffset = value; OnPropertyChanged(); } }

    public int XAbsToleranceMm { get => _cfg.XAbsFineTune.ToleranceMm; set { _cfg.XAbsFineTune.ToleranceMm = value; OnPropertyChanged(); } }
    public int XAbsMaxAdjustMm { get => _cfg.XAbsFineTune.MaxAdjustMm; set { _cfg.XAbsFineTune.MaxAdjustMm = value; OnPropertyChanged(); } }
    public int YAbsToleranceMm { get => _cfg.YAbsFineTune.ToleranceMm; set { _cfg.YAbsFineTune.ToleranceMm = value; OnPropertyChanged(); } }
    public int YAbsMaxAdjustMm { get => _cfg.YAbsFineTune.MaxAdjustMm; set { _cfg.YAbsFineTune.MaxAdjustMm = value; OnPropertyChanged(); } }
    public int AbsFineTuneBeforeReadSettleDelayMs
    {
        get => _cfg.AbsFineTuneVerification.BeforeReadSettleDelayMs;
        set { _cfg.AbsFineTuneVerification.BeforeReadSettleDelayMs = value; OnPropertyChanged(); }
    }
    public int AbsFineTuneStableSampleCount
    {
        get => _cfg.AbsFineTuneVerification.StableSampleCount;
        set { _cfg.AbsFineTuneVerification.StableSampleCount = value; OnPropertyChanged(); }
    }
    public int AbsFineTuneStableSampleIntervalMs
    {
        get => _cfg.AbsFineTuneVerification.StableSampleIntervalMs;
        set { _cfg.AbsFineTuneVerification.StableSampleIntervalMs = value; OnPropertyChanged(); }
    }
    public int AbsFineTuneStableRangeMm
    {
        get => _cfg.AbsFineTuneVerification.StableRangeMm;
        set { _cfg.AbsFineTuneVerification.StableRangeMm = value; OnPropertyChanged(); }
    }
    public int AbsFineTuneAfterMoveMinSettleDelayMs
    {
        get => _cfg.AbsFineTuneVerification.AfterMoveMinSettleDelayMs;
        set { _cfg.AbsFineTuneVerification.AfterMoveMinSettleDelayMs = value; OnPropertyChanged(); }
    }
    public int PressureStopNormalPositionToleranceMm
    {
        get => _cfg.Safety.PressureStopNormalPositionToleranceMm;
        set { _cfg.Safety.PressureStopNormalPositionToleranceMm = value; OnPropertyChanged(); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  初始化 — 每行持有对 _cfg 内对象的直接引用，修改即时生效
    // ═══════════════════════════════════════════════════════════════
    private void RefreshTabs()
    {
        // Tab1 — 天车速度：每行直接引用 _cfg.CraneSpeeds[n] 里的 AxisSpeed 对象
        CraneSpeedRows.Clear();
        for (int i = 1; i <= 5; i++)
        {
            if (!_cfg.CraneSpeeds.TryGetValue(i, out var sec))
                _cfg.CraneSpeeds[i] = sec = new MotionConfig.AbsMoveSection();
            CraneSpeedRows.Add(new CraneSpeedRow(i, sec));
        }

        // Tab2 — 机械手速度
        ManipulatorSpeedRows.Clear();
        for (int i = 1; i <= 3; i++)
        {
            if (!_cfg.ManipulatorSpeeds.TryGetValue(i, out var sec))
                _cfg.ManipulatorSpeeds[i] = sec = new MotionConfig.AbsMoveSection();
            ManipulatorSpeedRows.Add(new ManipulatorSpeedRow(i, sec));
        }

        // Tab3 — 斜床参数：每行直接引用字典
        SkewBedRows.Clear();
        var cd = _cfg.SkewBed.CenterDistances;
        var ml = _cfg.SkewBed.MaxWorkpieceLengthMm;
        foreach (var code in MotionConfig.SkewBedSection.GetBedCodesForLine(1))
            SkewBedRows.Add(new SkewBedParamRow(code, 1, cd, ml));
        foreach (var code in MotionConfig.SkewBedSection.GetBedCodesForLine(2))
            SkewBedRows.Add(new SkewBedParamRow(code, 2, cd, ml));

        // Tab3 — 研磨机最大加工长度（mm）
        GrindingLengthRows.Clear();
        foreach (var code in new[] { "ST701", "ST702", "ST703", "ST704" })
            GrindingLengthRows.Add(new GrindingLengthRow(code, _cfg.Grinding.MaxWorkpieceLengthMm));

        // Tab4 — 机械手坐标：每行直接引用 ArmCoords 字典
        ArmCoordRows.Clear();
        var armCoords = _cfg.Balancing.ArmCoords;
        foreach (var key in new[] { "M817", "M818", "M710", "M700", "M821", "M720" })
        {
            string desc = key switch
            {
                "M817" => "1号线动平衡下料架 ST019",
                "M818" => "2号线动平衡下料架 ST020",
                "M710" => "动平衡机入口 ST008",
                "M700" => "动平衡机出口 ST009",
                "M821" => "2号线短板中转 ST021",
                "M720" => "研磨上料架入口 ST010",
                _ => key
            };
            ArmCoordRows.Add(new ArmCoordRow(key, desc, armCoords));
        }

        // Tab5 — 编码器标定
        RefreshStationTargetRows();

        // Tab6 — 直径补偿
        RefreshDiameterOffsetRows();

        // Tab6 — PropertyChanged 驱动 UI 刷新
        OnPropertyChanged(nameof(XAbsFineTuneEnabled));
        OnPropertyChanged(nameof(YAbsFineTuneEnabled));
        OnPropertyChanged(nameof(AbsFineTuneBeforeReadSettleDelayMs));
        OnPropertyChanged(nameof(AbsFineTuneStableSampleCount));
        OnPropertyChanged(nameof(AbsFineTuneStableSampleIntervalMs));
        OnPropertyChanged(nameof(AbsFineTuneStableRangeMm));
        OnPropertyChanged(nameof(AbsFineTuneAfterMoveMinSettleDelayMs));
        OnPropertyChanged(nameof(Crane1HomeX)); OnPropertyChanged(nameof(Crane2HomeX));
        OnPropertyChanged(nameof(Crane3HomeX)); OnPropertyChanged(nameof(Crane4HomeX));
        OnPropertyChanged(nameof(Crane5HomeX));
    }

    private void RefreshStationTargetRows()
    {
        StationTargetRows.Clear();
        if (!_cfg.XAbsFineTune.StationTargets.TryGetValue(_selectedCraneNo, out var xTargets))
        {
            xTargets = new Dictionary<string, int>();
            _cfg.XAbsFineTune.StationTargets[_selectedCraneNo] = xTargets;
        }
        if (!_cfg.YAbsFineTune.StationTargets.TryGetValue(_selectedCraneNo, out var yTargets))
        {
            yTargets = new Dictionary<string, int>();
            _cfg.YAbsFineTune.StationTargets[_selectedCraneNo] = yTargets;
        }

        foreach (string stationCode in xTargets.Keys.Union(yTargets.Keys).OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
        {
            if (!xTargets.ContainsKey(stationCode)) xTargets[stationCode] = -1;
            if (!yTargets.ContainsKey(stationCode)) yTargets[stationCode] = -1;
            StationTargetRows.Add(new StationTargetRow(stationCode, xTargets, yTargets));
        }
    }

    private static readonly Dictionary<string, string> DiameterStationNames = new()
    {
        ["boring1"] = "1号线双头镗",
        ["boring2"] = "2号线双头镗",
        ["ST108"] = "1号斜床",
        ["ST109"] = "2号斜床",
        ["ST110"] = "4号斜床",
        ["ST111"] = "3号斜床",
        ["ST112"] = "5号斜床(FANUC)",
        ["ST606"] = "6号斜床",
        ["ST607"] = "7号斜床",
        ["ST608"] = "8号斜床",
        ["ST609"] = "9号斜床",
        ["ST610"] = "10号斜床",
    };

    private void RefreshDiameterOffsetRows()
    {
        DiameterOffsetRows.Clear();
        foreach (var kv in _cfg.DiameterOffsets)
        {
            var name = DiameterStationNames.TryGetValue(kv.Key, out var n) ? n : kv.Key;
            DiameterOffsetRows.Add(new DiameterOffsetRow(kv.Key, name, _cfg.DiameterOffsets));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  保存 — 只需写 JSON，配置对象已被实时写入
    // ═══════════════════════════════════════════════════════════════
    private void Save()
    {
        try
        {
            // 编码器标定页需先同步（行改的是字典里的值，但切换天车时可能丢新行）
            SyncStationTargets();
            _cfg.Save();
            MessageBox.Show("配置已保存到文件。\n所有修改已即时生效，无需重启。",
                "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>编码器标定行切换天车号前，把当前行的X/Y值写回对应字典。</summary>
    private void SyncStationTargets()
    {
        if (!_cfg.XAbsFineTune.StationTargets.TryGetValue(_selectedCraneNo, out var xTargets))
            _cfg.XAbsFineTune.StationTargets[_selectedCraneNo] = xTargets = new Dictionary<string, int>();
        if (!_cfg.YAbsFineTune.StationTargets.TryGetValue(_selectedCraneNo, out var yTargets))
            _cfg.YAbsFineTune.StationTargets[_selectedCraneNo] = yTargets = new Dictionary<string, int>();
        foreach (var row in StationTargetRows)
        {
            xTargets[row.StationCode] = row.TargetX;
            yTargets[row.StationCode] = row.TargetY;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  读当前位置
    // ═══════════════════════════════════════════════════════════════
    private async Task ReadCurrentEncoderAsync(string? stationCode, string axis)
    {
        if (string.IsNullOrWhiteSpace(stationCode) || _craneProvider == null) return;
        try
        {
            var crane = await _craneProvider(_selectedCraneNo);
            if (crane == null || !crane.IsConnected)
            {
                MessageBox.Show($"天车 {_selectedCraneNo} 号未连接，无法读取当前位置。",
                    "读取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            using var cts = new CancellationTokenSource(5000);
            var status = await crane.ReadStatusAsync(cts.Token);
            if (status == null)
            {
                MessageBox.Show("无法读取天车当前位置。", "读取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var row = StationTargetRows.FirstOrDefault(r => r.StationCode == stationCode);
            if (row != null)
            {
                if (axis == "X")
                    row.TargetX = status.XEncoderAbs; // D5014~D5015: X轴绝对编码器标定值
                else
                    row.TargetY = status.YEncoderAbs; // D5020: Y轴绝对编码器标定值
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"读取当前位置失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private int GetHomeX(int craneNo) => _cfg.CraneHomeX.TryGetValue(craneNo, out var x) ? x : 1000;
    private void SetHomeX(int craneNo, int value) { _cfg.CraneHomeX[craneNo] = value; OnPropertyChanged(); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommand(Action<object?> execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
        // 此命令始终可执行，不保存无用的事件委托字段。
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}

internal static class ConfigStationNames
{
    private static readonly IReadOnlyDictionary<string, string> Names =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ST713"] = "1号线货叉 Pos3取料位",
            ["ST107"] = "1号线打号机",
            ["ST105"] = "1号线中转架1",
            ["ST101"] = "1号线中转架2",
            ["ST106"] = "1号线中转架3",
            ["ST108"] = "1号线斜床1",
            ["ST109"] = "1号线斜床2",
            ["ST111"] = "1号线斜床3",
            ["ST110"] = "1号线斜床4",
            ["ST112"] = "1号线斜床5",
            ["ST019"] = "1号线动平衡下料架",
            ["ST010"] = "研磨上料架1号位",

            ["ST714"] = "2号线货叉 Pos3取料位",
            ["ST502"] = "2号线打号机",
            ["ST016"] = "2号线中转架1",
            ["ST017"] = "2号线中转架2",
            ["ST018"] = "2号线中转架3",
            ["ST606"] = "2号线斜床1",
            ["ST607"] = "2号线斜床2",
            ["ST608"] = "2号线斜床3",
            ["ST609"] = "2号线斜床4",
            ["ST610"] = "2号线斜床5",
            ["ST020"] = "2号线动平衡下料架",
            ["ST021"] = "2号线短板中转位",

            ["ST709"] = "研磨上料架3号位",
            ["ST701"] = "研磨机1（新代）",
            ["ST702"] = "研磨机2（新代）",
            ["ST703"] = "研磨机3（西门子）",
            ["ST704"] = "研磨机4（西门子）",
            ["ST710"] = "研磨下料架",
        };

    public static string Format(string stationCode)
        => Names.TryGetValue(stationCode, out var name) ? $"{stationCode} - {name}" : stationCode;
}

// ═══════════════════════════════════════════════════════════════════
//  行 ViewModel — 每个 setter 直接写入 _cfg 内对应对象，修改即时生效
// ═══════════════════════════════════════════════════════════════════

public sealed class CraneSpeedRow : INotifyPropertyChanged
{
    private readonly MotionConfig.AbsMoveSection _sec;
    public int CraneNo { get; }
    public string Label => $"{CraneNo}号天车";

    public CraneSpeedRow(int no, MotionConfig.AbsMoveSection sec)
    { CraneNo = no; _sec = sec; }

    public int XSpeed  { get => _sec.X.Speed;  set { _sec.X.Speed  = value; OnProp(); } }
    public int XAccel  { get => _sec.X.Accel;  set { _sec.X.Accel  = value; OnProp(); } }
    public int XDecel  { get => _sec.X.Decel;  set { _sec.X.Decel  = value; OnProp(); } }
    public int YSpeed  { get => _sec.Y.Speed;  set { _sec.Y.Speed  = value; OnProp(); } }
    public int YAccel  { get => _sec.Y.Accel;  set { _sec.Y.Accel  = value; OnProp(); } }
    public int YDecel  { get => _sec.Y.Decel;  set { _sec.Y.Decel  = value; OnProp(); } }
    public int ZSpeed  { get => _sec.Z.Speed;  set { _sec.Z.Speed  = value; OnProp(); } }
    public int ZAccel  { get => _sec.Z.Accel;  set { _sec.Z.Accel  = value; OnProp(); } }
    public int ZDecel  { get => _sec.Z.Decel;  set { _sec.Z.Decel  = value; OnProp(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnProp([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class ManipulatorSpeedRow : INotifyPropertyChanged
{
    private readonly MotionConfig.AbsMoveSection _sec;
    public int ManipulatorNo { get; }
    public string Label => $"{ManipulatorNo}号机械手";

    public ManipulatorSpeedRow(int no, MotionConfig.AbsMoveSection sec)
    { ManipulatorNo = no; _sec = sec; }

    public int YSpeed  { get => _sec.Y.Speed;  set { _sec.Y.Speed  = value; OnProp(); } }
    public int YAccel  { get => _sec.Y.Accel;  set { _sec.Y.Accel  = value; OnProp(); } }
    public int YDecel  { get => _sec.Y.Decel;  set { _sec.Y.Decel  = value; OnProp(); } }
    public int ZSpeed  { get => _sec.Z.Speed;  set { _sec.Z.Speed  = value; OnProp(); } }
    public int ZAccel  { get => _sec.Z.Accel;  set { _sec.Z.Accel  = value; OnProp(); } }
    public int ZDecel  { get => _sec.Z.Decel;  set { _sec.Z.Decel  = value; OnProp(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnProp([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class SkewBedParamRow : INotifyPropertyChanged
{
    private readonly Dictionary<string, int> _centerDist, _maxLen;
    public string StationCode { get; }
    public string StationDisplayName => ConfigStationNames.Format(StationCode);
    public int Line { get; }
    public string LineLabel => $"{Line}号线";

    public SkewBedParamRow(string code, int line, Dictionary<string, int> cd, Dictionary<string, int> ml)
    { StationCode = code; Line = line; _centerDist = cd; _maxLen = ml; }

    public int CenterDistance
    {
        get => _centerDist.TryGetValue(StationCode, out var v) ? v : 1450;
        set { _centerDist[StationCode] = value; OnProp(); }
    }
    public int MaxWorkpieceLength
    {
        get => _maxLen.TryGetValue(StationCode, out var v) ? v : 1300;
        set { _maxLen[StationCode] = value; OnProp(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnProp([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class GrindingLengthRow : INotifyPropertyChanged
{
    private readonly Dictionary<string, int> _maxLen;
    public string StationCode { get; }
    public string StationDisplayName => ConfigStationNames.Format(StationCode);

    public GrindingLengthRow(string stationCode, Dictionary<string, int> maxLen)
    {
        StationCode = stationCode;
        _maxLen = maxLen;
    }

    /// <summary>最大加工长度，单位mm；0表示当前机台禁止自动派发。</summary>
    public int MaxWorkpieceLength
    {
        get => _maxLen.TryGetValue(StationCode, out var value) ? value : 0;
        set { _maxLen[StationCode] = value; OnProp(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnProp([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class ArmCoordRow : INotifyPropertyChanged
{
    private readonly Dictionary<string, MotionConfig.BalancingArmCoord> _dict;
    public string SignalCode { get; }
    public string Description { get; }

    public ArmCoordRow(string code, string desc, Dictionary<string, MotionConfig.BalancingArmCoord> dict)
    { SignalCode = code; Description = desc; _dict = dict; }

    public int Y
    {
        get => _dict.TryGetValue(SignalCode, out var ac) ? ac.Y : 0;
        set { EnsureEntry().Y = value; OnProp(); }
    }
    public int Z
    {
        get => _dict.TryGetValue(SignalCode, out var ac) ? ac.Z : 0;
        set { EnsureEntry().Z = value; OnProp(); }
    }

    private MotionConfig.BalancingArmCoord EnsureEntry()
    {
        if (!_dict.TryGetValue(SignalCode, out var ac))
            _dict[SignalCode] = ac = new MotionConfig.BalancingArmCoord();
        return ac;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnProp([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class DiameterOffsetRow : INotifyPropertyChanged
{
    private readonly Dictionary<string, double> _dict;
    public string StationCode { get; }
    public string StationName { get; }

    public DiameterOffsetRow(string code, string name, Dictionary<string, double> dict)
    { StationCode = code; StationName = name; _dict = dict; }

    public double OffsetMm
    {
        get => _dict.TryGetValue(StationCode, out var v) ? v : 0.0;
        set { _dict[StationCode] = value; OnProp(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnProp([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class StationTargetRow : INotifyPropertyChanged
{
    private readonly Dictionary<string, int> _xTargets;
    private readonly Dictionary<string, int> _yTargets;
    public string StationCode { get; }
    public string StationDisplayName => ConfigStationNames.Format(StationCode);

    public StationTargetRow(string code, Dictionary<string, int> xTargets, Dictionary<string, int> yTargets)
    { StationCode = code; _xTargets = xTargets; _yTargets = yTargets; }

    public int TargetX
    {
        get => _xTargets.TryGetValue(StationCode, out var v) ? v : -1;
        set { _xTargets[StationCode] = value; OnProp(); }
    }

    public int TargetY
    {
        get => _yTargets.TryGetValue(StationCode, out var v) ? v : -1;
        set { _yTargets[StationCode] = value; OnProp(); }
    }

    public string TargetDisplay => TargetX == -1 ? "(跳过)" : TargetX.ToString();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnProp([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
