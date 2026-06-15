
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AutomaticOnlineHostComputer.Communication.Clients;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Communication.Models;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

public sealed class HomeViewModel : ObservableObject
{
    private readonly ManagementQueryService _queryService;
    private readonly PositionUpdateService _positionService;
    private readonly ProductionFlowEngine _flowEngine;
    private readonly CraneConnectionCache _craneCache;
    private readonly MotionConfig _cfg;
    private readonly ManipulatorConnectionCache _manipulatorCache;
    private readonly McConnectionCache _mcCache = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private bool _enginesInitialized;

    /// <summary>工位坐标(站号→MachineRow), 供弹窗查询</summary>
    private Dictionary<string, MachineManagementRowVm> _stationDict =
        new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, MachineManagementRowVm> StationCoords => _stationDict;
    public CraneConnectionCache SharedCraneCache => _craneCache;
    public MotionConfig SharedMotionConfig => _cfg;

    /// <summary>页面是否正在加载（绑定到启动动画）</summary>
    // ═══════════════════════════════════════════════════════════════
    //  研磨流程引擎控制
    // ═══════════════════════════════════════════════════════════════

    /// <summary>启动/暂停研磨自动流程</summary>
    private async Task GrindingToggleAsync()
    {
        if (_grindingEngine == null) { Console.WriteLine("[HomeViewModel] ✘ 研磨引擎未初始化"); return; }

        if (_isGrindingRunning)
        {
            Console.WriteLine("[HomeViewModel] ▶ 【暂停研磨】");
            await _grindingEngine.PauseAsync();
            IsGrindingRunning = false;
            OnPropertyChanged(nameof(GrindingCachedCount));
        }
        else
        {
            if (_grindingEngine.IsRunning && _grindingEngine.IsPaused)
            {
                Console.WriteLine("[HomeViewModel] ▶ 【恢复研磨】");
                _grindingEngine.Resume();
            }
            else
            {
                Console.WriteLine("[HomeViewModel] ▶ 【启动研磨】");
                _grindingEngine.Start();
            }
            IsGrindingRunning = true;
            OnPropertyChanged(nameof(GrindingCachedCount));
        }
    }

    /// <summary>将当前工件参数写入缓存</summary>
    private async Task WriteCacheAsync()
    {
        if (_grindingEngine == null)
        {
            Console.WriteLine("[HomeViewModel] ✘ 研磨引擎未初始化");
            return;
        }
        Console.WriteLine($"[HomeViewModel] ▶ 点击按钮【写入缓存】 直径={CachedDiameter} 版孔={(CachedBoreType == 1 ? "大孔" : "小孔")} 长度={CachedLength}");
        _grindingEngine.EnqueueWorkpiece(CachedDiameter, CachedBoreType, CachedLength);
        OnPropertyChanged(nameof(GrindingCachedCount));
        await Task.CompletedTask;
    }

    /// <summary>清空工件缓存</summary>
    private async Task ClearCacheAsync()
    {
        Console.WriteLine("[HomeViewModel] ▶ 点击按钮【清空缓存】");
        _grindingEngine?.ClearCache();
        OnPropertyChanged(nameof(GrindingCachedCount));
        await Task.CompletedTask;
    }

    /// <summary>测试: 手动给1号线中转架1注入工件缓存</summary>
    private async Task TestRearPickupAsync()
    {
        Console.WriteLine("[HomeViewModel] ▶ 点击【测试1号线后天车取料】");
        if (_line1RearEngine == null) { Console.WriteLine("[HomeViewModel] ⚠ 1号线后端引擎未创建"); return; }
        var wp = new WorkpieceCache
        {
            Diameter = 147, Length = 700, BoreType = 100,
            MarkingContent = "TEST", LeftPlugThickness = 14, RightPlugThickness = 14,
            SkipBoring = false, BoringProcess = "粗镗"
        };
        _line1RearEngine.EnqueueWorkpiece("ST105", wp);
        Console.WriteLine($"[HomeViewModel] 已注入测试工件到1号线中转架1(ST105) d=147 L=700");
        await Task.CompletedTask;
    }

    /// <summary>测试: 手动给2号线中转架4注入工件缓存</summary>
    private async Task TestRearPickup2Async()
    {
        Console.WriteLine("[HomeViewModel] ▶ 点击【测试2号线后天车取料】");
        if (_line2RearEngine == null) { Console.WriteLine("[HomeViewModel] ⚠ 2号线后端引擎未创建"); return; }
        var wp = new WorkpieceCache
        {
            Diameter = 150, Length = 795, BoreType = 100,
            MarkingContent = "TEST2", LeftPlugThickness = 14, RightPlugThickness = 14,
            SkipBoring = false, BoringProcess = "粗镗"
        };
        _line2RearEngine.EnqueueWorkpiece("ST016", wp);
        Console.WriteLine($"[HomeViewModel] 已注入测试工件到2号线中转架4(ST016) d=147 L=700");
        await Task.CompletedTask;
    }

    /// <summary>测试: 手动写入动平衡工件缓存(M817/M818/M821), 模拟后天车放料</summary>
    private async Task TestBalancingRackPlacedAsync()
    {
        Console.WriteLine($"[HomeViewModel] ▶ 点击【测试动平衡】 位置={SelectedBalancingPosition} d={BalancingTestDiameter} L={BalancingTestLength}");
        if (_line1BalancingEngine == null)
        {
            Console.WriteLine("[HomeViewModel] ⚠ 动平衡引擎未创建"); return;
        }

        if (string.IsNullOrEmpty(SelectedBalancingPosition))
        {
            Console.WriteLine("[HomeViewModel] ⚠ 未选择位置"); return;
        }
        var wp = new WorkpieceCache
        {
            Diameter = BalancingTestDiameter, Length = BalancingTestLength, BoreType = 100
        };
        _line1BalancingEngine.SetBalancingWp(SelectedBalancingPosition, wp);
        Console.WriteLine($"[HomeViewModel] 已注入测试工件 → {SelectedBalancingPosition} d={BalancingTestDiameter} L={BalancingTestLength}");
        await Task.CompletedTask;
    }

    /// <summary>写入研磨参数到选中研磨机（0=Grinder1 1=Grinder2 2=Grinder3 3=Grinder4）。</summary>
    private async Task WriteGrinderParamsAsync()
    {
        var grinders = new[] { Grinder1, Grinder2, Grinder3, Grinder4 };
        if (GrinderParamIndex < 0 || GrinderParamIndex > 3)
        {
            Console.WriteLine($"[HomeViewModel] ✘ 研磨机编号无效：{GrinderParamIndex}");
            return;
        }
        var card = grinders[GrinderParamIndex];
        Console.WriteLine($"[HomeViewModel] ▶ 写入参数到 {card.Title}：直径={GrinderDiameter} 版孔={(GrinderBoreType == 1 ? "大孔" : "小孔")} 长度={GrinderLength}");
        bool ok = await card.WriteParamsAsync(GrinderDiameter, GrinderBoreType, GrinderLength);
        Console.WriteLine($"[HomeViewModel] {(ok ? "✔" : "✘")} 参数写入{(ok ? "成功" : "失败")}");
    }

    private bool _isLoading = true;
    public bool IsLoading { get => _isLoading; private set => SetField(ref _isLoading, value); }
    private CancellationTokenSource? _erpTaskImportCts;
    private Task? _erpTaskImportTask;
    private bool _isErpTaskImportRunning;
    private string _erpTaskImportStatus = "ERP监听：未启动";
    public bool IsErpTaskImportRunning
    {
        get => _isErpTaskImportRunning;
        private set
        {
            if (SetField(ref _isErpTaskImportRunning, value))
                OnPropertyChanged(nameof(ErpTaskImportToggleText));
        }
    }
    public string ErpTaskImportToggleText => IsErpTaskImportRunning ? "停止检测ERP下发任务" : "开始检测ERP下发任务";
    public string ErpTaskImportStatus { get => _erpTaskImportStatus; private set => SetField(ref _erpTaskImportStatus, value); }

    public HomeViewModel(ManagementQueryService queryService, PositionUpdateService positionService)
    {
        Console.WriteLine("HomeViewModel页面启动");
        _queryService = queryService;
        _positionService = positionService;
        _craneCache = new CraneConnectionCache();
        _manipulatorCache = new ManipulatorConnectionCache();
        _cfg = MotionConfig.Load();  // 提前加载，引擎创建和UI同步都要用

        // 引擎依赖天车/机械手缓存，由 HomeVM 创建并持有引用
        _flowEngine = new ProductionFlowEngine(_craneCache, _manipulatorCache, _positionService);

        Crane1F = new CraneCardViewModel(1, _craneCache, _positionService);
        Crane1R = new CraneCardViewModel(2, _craneCache, _positionService);
        Crane2F = new CraneCardViewModel(3, _craneCache, _positionService);
        Crane2R = new CraneCardViewModel(4, _craneCache, _positionService);
        CraneGL = new CraneCardViewModel(5, _craneCache, _positionService);

        ManualControl = new CraneManualControlViewModel(_craneCache, _manipulatorCache, _positionService);

        Manipulator1 = new ManipulatorCardViewModel(1, _manipulatorCache, _positionService);
        Manipulator2 = new ManipulatorCardViewModel(2, _manipulatorCache, _positionService);
        Manipulator3 = new ManipulatorCardViewModel(3, _manipulatorCache, _positionService);

        Grinder1 = new GrinderCardViewModel("研磨机1(新代)",   "", PlcGrinderService.GrinderType.TypeB);
        Grinder2 = new GrinderCardViewModel("研磨机2(新代)",   "", PlcGrinderService.GrinderType.TypeB);
        Grinder3 = new GrinderCardViewModel("研磨机3(西门子)", "", PlcGrinderService.GrinderType.TypeA);
        Grinder4 = new GrinderCardViewModel("研磨机4(西门子)", "", PlcGrinderService.GrinderType.TypeA);

        WriteGrinderParamsCommand = new AsyncRelayCommand(WriteGrinderParamsAsync, nameof(WriteGrinderParamsCommand));
        SkewBedToolSettingRows = CreateSkewBedToolSettingRows();

        GrindingToggleCommand = new AsyncRelayCommand(GrindingToggleAsync, nameof(GrindingToggleCommand));
        Line1ToggleCommand = new AsyncRelayCommand(Line1ToggleAsync, nameof(Line1ToggleCommand));
        Line2ToggleCommand = new AsyncRelayCommand(Line2ToggleAsync, nameof(Line2ToggleCommand));
        BalancingToggleCommand = new AsyncRelayCommand(BalancingToggleAsync, nameof(BalancingToggleCommand));
        WriteCacheCommand = new AsyncRelayCommand(WriteCacheAsync, nameof(WriteCacheCommand));
        ClearCacheCommand = new AsyncRelayCommand(ClearCacheAsync, nameof(ClearCacheCommand));
        TestRearPickupCommand = new AsyncRelayCommand(TestRearPickupAsync, nameof(TestRearPickupCommand));
        TestRearPickup2Command = new AsyncRelayCommand(TestRearPickup2Async, nameof(TestRearPickup2Command));
        TestBalancingRackPlacedCommand = new AsyncRelayCommand(TestBalancingRackPlacedAsync, nameof(TestBalancingRackPlacedCommand));
        ErpTaskImportToggleCommand = new AsyncRelayCommand(ErpTaskImportToggleAsync, nameof(ErpTaskImportToggleCommand));

        // 测试动平衡默认值
        BalancingTestDiameter = 147;
        BalancingTestLength = 700;
        SelectedBalancingPosition = "M817";

        Console.WriteLine("[HomeViewModel] 初始化完成：天车+机械手+研磨机+手动控制+流程引擎 VM已创建。");
    }

    public Dictionary<string, string> IpMap { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 全厂状态总览卡片字典。key = 站号（如 ST401），value = 该站位的卡片 ViewModel。
    /// LoadAsync 时从数据库 machine 表填充全新字典替换，触发 WPF 刷新所有站卡绑定。
    /// </summary>
    private Dictionary<string, StationCardViewModel> _stationCards = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, StationCardViewModel> StationCards
    {
        get => _stationCards;
        private set => SetField(ref _stationCards, value);
    }

    public CraneCardViewModel Crane1F { get; }
    public CraneCardViewModel Crane1R { get; }
    public CraneCardViewModel Crane2F { get; }
    public CraneCardViewModel Crane2R { get; }
    public CraneCardViewModel CraneGL { get; }

    public CraneManualControlViewModel ManualControl { get; }

    /// <summary>10台斜床的设备级对刀开关, 勾选后只覆盖该斜床后续写入的加工模式。</summary>
    public ObservableCollection<SkewBedToolSettingRow> SkewBedToolSettingRows { get; }

    public ManipulatorCardViewModel Manipulator1 { get; }
    public ManipulatorCardViewModel Manipulator2 { get; }
    public ManipulatorCardViewModel Manipulator3 { get; }

    public GrinderCardViewModel Grinder1 { get; private set; }
    public GrinderCardViewModel Grinder2 { get; private set; }
    public GrinderCardViewModel Grinder3 { get; private set; }
    public GrinderCardViewModel Grinder4 { get; private set; }

    // ═══════════════════════════════════════════════════════════════
    //  研磨参数写入面板（测试用）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>选中写入哪台研磨机：0=1号 1=2号 2=3号 3=4号</summary>
    private int _grinderParamIndex;
    public int GrinderParamIndex
    {
        get => _grinderParamIndex;
        set => SetField(ref _grinderParamIndex, value);
    }

    /// <summary>版辊直径（mm），Word 无符号 16 位</summary>
    private int _grinderDiameter = 200;
    public int GrinderDiameter
    {
        get => _grinderDiameter;
        set => SetField(ref _grinderDiameter, value);
    }

    /// <summary>版孔类型：1=大孔 2=小孔</summary>
    private int _grinderBoreType = 1;
    public int GrinderBoreType
    {
        get => _grinderBoreType;
        set => SetField(ref _grinderBoreType, value);
    }

    /// <summary>版棍长度（mm），Word 无符号 16 位</summary>
    private int _grinderLength = 1000;
    public int GrinderLength
    {
        get => _grinderLength;
        set => SetField(ref _grinderLength, value);
    }

    /// <summary>写入研磨参数到选中研磨机</summary>
    public ICommand WriteGrinderParamsCommand { get; }

    private ObservableCollection<SkewBedToolSettingRow> CreateSkewBedToolSettingRows()
    {
        var beds = new (string Code, string Name)[]
        {
            ("ST108", "1号线斜床1 ST108"),
            ("ST109", "1号线斜床2 ST109"),
            ("ST111", "1号线斜床3 ST111"),
            ("ST110", "1号线斜床4 ST110"),
            ("ST112", "1号线斜床5 ST112"),
            ("ST606", "2号线斜床1 ST606"),
            ("ST607", "2号线斜床2 ST607"),
            ("ST608", "2号线斜床3 ST608"),
            ("ST609", "2号线斜床4 ST609"),
            ("ST610", "2号线斜床5 ST610"),
        };

        return new ObservableCollection<SkewBedToolSettingRow>(
            beds.Select(b => new SkewBedToolSettingRow(
                b.Code,
                b.Name,
                _cfg.SkewBed.IsToolSettingEnabled(b.Code),
                OnSkewBedToolSettingChanged)));
    }

    private void OnSkewBedToolSettingChanged(SkewBedToolSettingRow row)
    {
        // 设备级对刀设置只保存站号开关; 任务里的斜床工艺保持原值, 便于追溯原始任务。
        _cfg.SkewBed.ToolSettingBeds[row.Code] = row.IsToolSetting;
        try
        {
            _cfg.Save();
            Console.WriteLine($"[HomeViewModel] 斜床对刀设置已保存: {row.Code}={(row.IsToolSetting ? "开启" : "关闭")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HomeViewModel] ⚠ 斜床对刀设置保存失败: {row.Code} {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  研磨自动流程引擎
    // ═══════════════════════════════════════════════════════════════

    private GrindingFlowEngine? _grindingEngine;

    /// <summary>是否正在运行研磨自动流程</summary>
    private bool _isGrindingRunning;
    public bool IsGrindingRunning { get => _isGrindingRunning; set { if (SetField(ref _isGrindingRunning, value)) OnPropertyChanged(nameof(GrindingToggleText)); } }

    /// <summary>启动/暂停按钮文本</summary>
    public string GrindingToggleText => _isGrindingRunning ? "暂停研磨" : "启动研磨";

    /// <summary>已缓存研磨工件数量</summary>
    public int GrindingCachedCount => _grindingEngine?.CachedCount ?? 0;

    // ── 1号线流程引擎 ────────────────────────────────────────────────
    private Line1FrontFlowEngine? _line1Engine;
    private Line1RearFlowEngine? _line1RearEngine;
    private BalancingFlowEngine? _line1BalancingEngine; // 机械手2动平衡流转(两条线共用)
    private SemaphoreSlim? _sharedTransferRackLock; // 1号线前后端共享中转架锁
    private CancellationTokenSource? _line1StatusCts;

    // ── 2号线流程引擎 ────────────────────────────────────────────────
    private Line2FrontFlowEngine? _line2Engine;
    private Line2RearFlowEngine? _line2RearEngine;
    private SemaphoreSlim? _sharedManipulatorLock; // 机械手1互斥锁(1号线+2号线共享)
    private SemaphoreSlim? _sharedTransferRackLock2; // 2号线前后端共享中转架锁
    private CancellationTokenSource? _line2StatusCts;
    private CancellationTokenSource? _grindingRackStatusCts;

    /// <summary>后台任务：从引擎 DeviceStatus 同步到 UI 卡片（不另建连接）</summary>
    private async Task SyncLine1StatusToCardsAsync(CancellationToken ct)
    {
        // XAML 显示码 → 数据来源:
        //   引擎托管(仅运行时刷): ST001→ST007, ST002→ST002, ST011→ST711, ST401→ST401, ST501→ST501, ST901→ST901, ST105/ST101/ST106→M811~M813
        //   第三部分镜像(一直刷): ST005→机械手2VM, ST006→机械手3VM
        //   研磨机(独立线程刷): ST701~ST704→GrinderPollLoop
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cards = StationCards;
                bool engineRunning = _line1Engine is { IsRunning: true };

                // ═══════════════════════════════════════════════════════
                //  A. 机械手2/3 状态镜像 — 一直刷新（从第三部分 VM 取）
                // ═══════════════════════════════════════════════════════
                SyncManipulatorMirror(cards, "ST005", Manipulator2, "机械手2");
                SyncManipulatorMirror(cards, "ST006", Manipulator3, "机械手3");

                // ═══════════════════════════════════════════════════════
                //  B. 引擎托管卡片 — 仅在1号线运行时刷新
                // ═══════════════════════════════════════════════════════
                if (engineRunning && _line1Engine!.DeviceStatus is { } ds)
                {
                    // 确保 XAML 显示码有对应卡片条目（已预建，此处兜底）
                    EnsureCard("ST001", "总上料架");
                    EnsureCard("ST002", "机械手1");
                    EnsureCard("ST011", "货叉1");
                    EnsureCard("ST901", "1号线天车前");
                    EnsureCard("ST401", "一号双头镗");
                    EnsureCard("ST501", "一号打号机");
                    EnsureCard("ST105", "1号线中转架1");
                    EnsureCard("ST101", "1号线中转架2");
                    EnsureCard("ST106", "1号线中转架3");
                    EnsureCard("ST108", "一号斜床");
                    EnsureCard("ST109", "二号斜床");
                    EnsureCard("ST111", "三号斜床");
                    EnsureCard("ST110", "四号斜床");
                    EnsureCard("ST112", "五号斜床");

                    // ── 总上料架 ST001 (MC:192.168.2.63:9000 M800~M816+D100) ──
                    if (cards.TryGetValue("ST001", out var c))
                    { c.ConnectedBrush = ds.RackConnected ? Brushes.LimeGreen : Brushes.Gray;
                      c.Status1 = ds.RackConnected ? (ds.RackRequestPickup ? "请求取料" : "空闲") : "断开";
                      c.Status1Brush = ds.RackRequestPickup ? Brushes.Orange : Brushes.Green;
                      c.Status2 = ds.RackConnected ? $"板长={ds.RackPlateLength}mm 中1={(ds.TransferRack1Free?"空":"满")} 2={(ds.TransferRack2Free?"空":"满")} 3={(ds.TransferRack3Free?"空":"满")}" : "—"; }

                    // ── 机械手1 ST002 (Modbus:192.168.2.85:502 D5000~D4522) ──
                    if (cards.TryGetValue("ST002", out var c2))
                    { c2.ConnectedBrush = ds.Manipulator1Connected ? Brushes.LimeGreen : Brushes.Gray;
                      c2.Status1 = ds.Manipulator1Connected ? (ds.Manipulator1Safe ? "安全位" : $"Y={ds.Manipulator1Y}") : "断开";
                      c2.Status1Brush = ds.Manipulator1Safe ? Brushes.Green : Brushes.Orange;
                      c2.Status2 = ds.Manipulator1Connected ? $"安全Y={_cfg.SkewBed.Manipulator1SafeY}" : "—"; }

                    // ── 货叉1 ST011 (MC:192.168.2.88:9000 M900~M915) ──
                    if (cards.TryGetValue("ST011", out var c3))
                    { c3.ConnectedBrush = ds.ForkConnected ? Brushes.LimeGreen : Brushes.Gray;
                      c3.Status1 = ds.ForkConnected ? (ds.ForkHasPlate ? "有版" : "无版") : "断开";
                      c3.Status1Brush = ds.ForkHasPlate ? Brushes.Orange : Brushes.Green;
                      c3.Status2 = ds.ForkConnected ? ds.ForkPosition : "—"; }

                    // ── 双头镗 ST401 (Syntec R6101~R6110) ──
                    if (cards.TryGetValue("ST401", out var c4))
                    {
                        c4.ConnectedBrush = ds.BoringConnected ? Brushes.LimeGreen : Brushes.Gray;
                        bool idle = ds.Boring_R6101 && !ds.Boring_R6103 && !ds.Boring_R6105 && !ds.Boring_R6107 && !ds.Boring_R6109;
                        c4.Status1 = ds.BoringConnected ? (idle ? "空闲" : "加工中") : "断开";
                        c4.Status1Brush = idle ? Brushes.Green : Brushes.Orange;
                        c4.Status2 = ds.BoringConnected
                            ? $"R6101={To01(ds.Boring_R6101)} R6103={To01(ds.Boring_R6103)} R6105={To01(ds.Boring_R6105)} R6107={To01(ds.Boring_R6107)} R6109={To01(ds.Boring_R6109)}"
                            : "—";
                    }

                    // ── 打号机 ST501 (文件握手:D:\1\ A/B/3) ──
                    if (cards.TryGetValue("ST501", out var c5))
                    { c5.ConnectedBrush = ds.MarkerConnected ? Brushes.LimeGreen : Brushes.Gray;
                      c5.Status1 = ds.MarkerConnected ? "就绪" : "断开";
                      c5.Status1Brush = ds.MarkerConnected ? Brushes.Green : Brushes.Gray;
                      c5.Status2 = ds.MarkerConnected ? @"\\192.168.2.67\1" : "—"; }

                    // ── 1号线天车前 ST901 (Modbus:192.168.2.81:502) ──
                    if (cards.TryGetValue("ST901", out var c6))
                    { c6.ConnectedBrush = ds.CraneFrontConnected ? Brushes.LimeGreen : Brushes.Gray;
                      c6.Status1 = ds.CraneFrontConnected ? "就绪" : "断开";
                      c6.Status1Brush = ds.CraneFrontConnected ? Brushes.Green : Brushes.Gray;
                      c6.Status2 = ds.CraneFrontConnected ? "192.168.2.81:502" : "—"; }

                    SetPlateCard(cards, "ST105", ds.RackConnected, !ds.TransferRack1Free, "M811", "192.168.2.63:9000");
                    SetPlateCard(cards, "ST101", ds.RackConnected, !ds.TransferRack2Free, "M812", "192.168.2.63:9000");
                    SetPlateCard(cards, "ST106", ds.RackConnected, !ds.TransferRack3Free, "M813", "192.168.2.63:9000");
                    SetPlateCard(cards, "ST019", ds.RackConnected && ds.M817SnapshotValid, ds.M817_HasPlate,
                        ds.M817SnapshotValid ? "M817" : "M817快照失败", "192.168.2.63:9000");

                }

                // ═══════════════════════════════════════════════════════
                //  C. 后端引擎状态 — 仅在1号线运行时刷新
                // ═══════════════════════════════════════════════════════
                if (engineRunning && _line1RearEngine?.DeviceStatus is { } rds)
                {
                    // 1号线天车后 ST102 (Modbus:192.168.2.82:502)
                    if (cards.TryGetValue("ST902", out var cr))
                    { cr.ConnectedBrush = rds.CraneRearConnected ? Brushes.LimeGreen : Brushes.Gray;
                      cr.Status1 = rds.CraneRearConnected ? "就绪" : "断开";
                      cr.Status1Brush = rds.CraneRearConnected ? Brushes.Green : Brushes.Gray;
                      cr.Status2 = rds.CraneRearConnected ? "192.168.2.82:502" : "—"; }

                    // 斜床 ST108~ST112 — 各自独立显示连接+状态+寄存器信号
                    if (cards.TryGetValue("ST108", out var sk1) && rds.Skew1Connected) { sk1.ConnectedBrush = Brushes.LimeGreen; sk1.Status1 = rds.Skew1State; sk1.Status1Brush = Brushes.Green; sk1.Status2 = rds.Skew1Signals; }
                    if (cards.TryGetValue("ST109", out var sk2) && rds.Skew2Connected) { sk2.ConnectedBrush = Brushes.LimeGreen; sk2.Status1 = rds.Skew2State; sk2.Status1Brush = Brushes.Green; sk2.Status2 = rds.Skew2Signals; }
                    if (cards.TryGetValue("ST111", out var sk3) && rds.Skew3Connected) { sk3.ConnectedBrush = Brushes.LimeGreen; sk3.Status1 = rds.Skew3State; sk3.Status1Brush = Brushes.Green; sk3.Status2 = rds.Skew3Signals; }
                    if (cards.TryGetValue("ST110", out var sk4) && rds.Skew4Connected) { sk4.ConnectedBrush = Brushes.LimeGreen; sk4.Status1 = rds.Skew4State; sk4.Status1Brush = Brushes.Green; sk4.Status2 = rds.Skew4Signals; }
                    if (cards.TryGetValue("ST112", out var sk5) && rds.Skew5Connected) { sk5.ConnectedBrush = Brushes.LimeGreen; sk5.Status1 = rds.Skew5State; sk5.Status1Brush = Brushes.Green; sk5.Status2 = rds.Skew5Signals; }

                }

                SyncBalancingCards(cards);
            }
            catch { }
            await Task.Delay(1500, ct);
        }
    }

    /// <summary>后台任务：从2号线引擎 DeviceStatus 同步到 UI 卡片</summary>
    private async Task SyncLine2StatusToCardsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cards = StationCards;
                bool engineRunning = _line2Engine is { IsRunning: true };

                if (engineRunning && _line2Engine!.DeviceStatus is { } ds)
                {
                    EnsureCard("ST712", "货叉2");
                    EnsureCard("ST402", "二号双头镗");
                    EnsureCard("ST502", "二号打号机");
                    EnsureCard("ST903", "2号线天车前");
                    EnsureCard("ST016", "2号线中转架1");
                    EnsureCard("ST017", "2号线中转架2");
                    EnsureCard("ST018", "2号线中转架3");
                    EnsureCard("ST606", "2号线斜床1");
                    EnsureCard("ST607", "2号线斜床2");
                    EnsureCard("ST608", "2号线斜床3");
                    EnsureCard("ST609", "2号线斜床4");
                    EnsureCard("ST610", "2号线斜床5");

                    // 货叉2 ST712
                    if (cards.TryGetValue("ST712", out var c3))
                    { c3.ConnectedBrush = ds.ForkConnected ? Brushes.LimeGreen : Brushes.Gray;
                      c3.Status1 = ds.ForkConnected ? (ds.ForkHasPlate ? "有版" : "无版") : "断开";
                      c3.Status1Brush = ds.ForkHasPlate ? Brushes.Orange : Brushes.Green;
                      c3.Status2 = ds.ForkConnected ? ds.ForkPosition : "—"; }

                    // 双头镗 ST402
                    if (cards.TryGetValue("ST402", out var c4))
                    { c4.ConnectedBrush = ds.BoringConnected ? Brushes.LimeGreen : Brushes.Gray;
                      bool idle = ds.Boring_R6101 && !ds.Boring_R6103 && !ds.Boring_R6105 && !ds.Boring_R6107 && !ds.Boring_R6109;
                      c4.Status1 = ds.BoringConnected ? (idle ? "空闲" : "加工中") : "断开";
                      c4.Status1Brush = idle ? Brushes.Green : Brushes.Orange;
                      c4.Status2 = $"R6101={To01(ds.Boring_R6101)} R6103={To01(ds.Boring_R6103)} R6105={To01(ds.Boring_R6105)} R6107={To01(ds.Boring_R6107)} R6109={To01(ds.Boring_R6109)}"; }

                    // 打号机 ST502
                    if (cards.TryGetValue("ST502", out var c5))
                    { c5.ConnectedBrush = ds.MarkerConnected ? Brushes.LimeGreen : Brushes.Gray;
                      c5.Status1 = ds.MarkerConnected ? "就绪" : "断开";
                      c5.Status1Brush = ds.MarkerConnected ? Brushes.Green : Brushes.Gray;
                      c5.Status2 = ds.MarkerConnected ? @"\\192.168.2.74\1" : "—"; }

                    // 天车前 ST903
                    if (cards.TryGetValue("ST903", out var c6))
                    { c6.ConnectedBrush = ds.CraneFrontConnected ? Brushes.LimeGreen : Brushes.Gray;
                      c6.Status1 = ds.CraneFrontConnected ? "就绪" : "断开";
                      c6.Status1Brush = ds.CraneFrontConnected ? Brushes.Green : Brushes.Gray;
                      c6.Status2 = ds.CraneFrontConnected ? "192.168.2.83:502" : "—"; }

                    // 中转架
                    SetPlateCard(cards, "ST016", ds.RackConnected, !ds.TransferRack4Free, "M814", "192.168.2.63:9000");
                    SetPlateCard(cards, "ST017", ds.RackConnected, !ds.TransferRack5Free, "M815", "192.168.2.63:9000");
                    SetPlateCard(cards, "ST018", ds.RackConnected, !ds.TransferRack6Free, "M816", "192.168.2.63:9000");
                    SetPlateCard(cards, "ST020", ds.RackConnected && ds.M818SnapshotValid, ds.M818_HasPlate,
                        ds.M818SnapshotValid ? "M818" : "M818快照失败", "192.168.2.63:9000");
                }

                // 后端引擎状态
                if (engineRunning && _line2RearEngine?.DeviceStatus is { } rds)
                {
                    EnsureCard("ST904", "2号线天车后");
                    if (cards.TryGetValue("ST904", out var cr))
                    { cr.ConnectedBrush = rds.CraneRearConnected ? Brushes.LimeGreen : Brushes.Gray;
                      cr.Status1 = rds.CraneRearConnected ? "就绪" : "断开";
                      cr.Status2 = rds.CraneRearConnected ? "192.168.2.84:502" : "—"; }

                    if (cards.TryGetValue("ST606", out var sk1) && rds.Skew1Connected) { sk1.ConnectedBrush = Brushes.LimeGreen; sk1.Status1 = rds.Skew1State; sk1.Status1Brush = Brushes.Green; sk1.Status2 = rds.Skew1Signals; }
                    if (cards.TryGetValue("ST607", out var sk2) && rds.Skew2Connected) { sk2.ConnectedBrush = Brushes.LimeGreen; sk2.Status1 = rds.Skew2State; sk2.Status1Brush = Brushes.Green; sk2.Status2 = rds.Skew2Signals; }
                    if (cards.TryGetValue("ST608", out var sk3) && rds.Skew3Connected) { sk3.ConnectedBrush = Brushes.LimeGreen; sk3.Status1 = rds.Skew3State; sk3.Status1Brush = Brushes.Green; sk3.Status2 = rds.Skew3Signals; }
                    if (cards.TryGetValue("ST609", out var sk4) && rds.Skew4Connected) { sk4.ConnectedBrush = Brushes.LimeGreen; sk4.Status1 = rds.Skew4State; sk4.Status1Brush = Brushes.Green; sk4.Status2 = rds.Skew4Signals; }
                    if (cards.TryGetValue("ST610", out var sk5) && rds.Skew5Connected) { sk5.ConnectedBrush = Brushes.LimeGreen; sk5.Status1 = rds.Skew5State; sk5.Status1Brush = Brushes.Green; sk5.Status2 = rds.Skew5Signals; }
                }

                SyncBalancingCards(cards);
            }
            catch { }
            await Task.Delay(1500, ct);
        }
    }

    /// <summary>StationCards 缺失时自动创建（不建TCP连接，引擎托管）。替换字典引用触发WPF PropertyChanged。</summary>
    private void EnsureCard(string code, string name)
    {
        if (!StationCards.ContainsKey(code))
        {
            var newDict = new Dictionary<string, StationCardViewModel>(StationCards, StringComparer.OrdinalIgnoreCase);
            newDict[code] = new StationCardViewModel(code, name, "引擎托管", 0, "引擎托管");
            StationCards = newDict;  // 替换引用 → PropertyChanged → WPF重新解析所有索引器绑定
            Console.WriteLine($"[HomeViewModel] +卡片 {code} {name} (已触发PropertyChanged)");
        }
    }

    /// <summary>将第三部分机械手卡片的状态镜像到第四部分 StatusCard（不建新连接，复用已有 VM）。</summary>
    private static string To01(bool b) => b ? "1" : "0";

    private static void SyncManipulatorMirror(Dictionary<string, StationCardViewModel> cards,
        string code, ManipulatorCardViewModel manipVm, string name)
    {
        if (!cards.TryGetValue(code, out var sc)) return;
        sc.ConnectedBrush = manipVm.ConnectedBrush;
        sc.Status1 = manipVm.Line1;          // "已连接，就绪" / "未连接" / "故障"
        sc.Status1Brush = manipVm.Line1Brush;
        sc.Status2 = $"{manipVm.Line5}";     // Y/Z 坐标
    }

    private static void SetPlateCard(Dictionary<string, StationCardViewModel> cards,
        string code, bool connected, bool hasPlate, string status2, string ipText)
    {
        if (!cards.TryGetValue(code, out var card)) return;
        card.ConnectedBrush = connected ? Brushes.LimeGreen : Brushes.Gray;
        card.Status1 = connected ? (hasPlate ? "有版" : "空闲") : "断开";
        card.Status1Brush = !connected ? Brushes.Gray : (hasPlate ? Brushes.Orange : Brushes.Green);
        card.Status2 = connected ? status2 : "—";
        card.IpText = connected ? ipText : "未连接";
    }

    private static void SetReadyCard(Dictionary<string, StationCardViewModel> cards,
        string code, bool connected, bool ready, string readyText, string notReadyText, string status2, string ipText)
    {
        if (!cards.TryGetValue(code, out var card)) return;
        card.ConnectedBrush = connected ? Brushes.LimeGreen : Brushes.Gray;
        card.Status1 = connected ? (ready ? readyText : notReadyText) : "断开";
        card.Status1Brush = !connected ? Brushes.Gray : (ready ? Brushes.Green : Brushes.Orange);
        card.Status2 = connected ? status2 : "—";
        card.IpText = connected ? ipText : "未连接";
    }

    private void SyncBalancingCards(Dictionary<string, StationCardViewModel> cards)
    {
        var engine = _line1BalancingEngine;
        if (engine?.IsRunning != true) return;

        bool mc63 = engine.Mc63Connected;
        SetPlateCard(cards, "ST019", mc63, engine.M817HasPlate, $"M817 M2={(engine.M2Busy ? "忙" : "闲")}", "192.168.2.63:9000");
        SetPlateCard(cards, "ST020", mc63, engine.M818HasPlate, $"M818 M2={(engine.M2Busy ? "忙" : "闲")}", "192.168.2.63:9000");
        SetPlateCard(cards, "ST021", mc63, engine.M821HasPlate, $"M821 M3={(engine.M3Busy ? "忙" : "闲")}", "192.168.2.63:9000");

        bool mc65 = engine.Mc65Connected;
        SetReadyCard(cards, "ST008", mc65, engine.M710CanPlace, "可放料", "不可放料", $"M710 M2={(engine.M2Busy ? "忙" : "闲")}", "192.168.2.65:9000");
        SetPlateCard(cards, "ST009", mc65, engine.M700HasPlate, $"M700 M3={(engine.M3Busy ? "忙" : "闲")}", "192.168.2.65:9000");
        SetReadyCard(cards, "ST010", mc65, engine.M720CanPlace, "可放料", "不可放料", $"M720 M3={(engine.M3Busy ? "忙" : "闲")}", "192.168.2.65:9000");
    }

    /// <summary>研磨上下料架状态变化快，独立刷新到大屏卡片，避免只停留在页面加载时探测值。</summary>
    private async Task SyncGrindingRackStatusToCardsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cards = StationCards;
                await RefreshGrindingFeedRackCardsAsync(cards, ct);
                await RefreshGrindingUnloadRackCardAsync(cards, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[HomeViewModel] 研磨料架状态同步异常: {ex.Message}");
            }

            await Task.Delay(1500, ct);
        }
    }

    private async Task RefreshGrindingFeedRackCardsAsync(Dictionary<string, StationCardViewModel> cards, CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(2000);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var client = await _mcCache.GetOrCreateAsync("192.168.2.65", 9000, linked.Token);

            var r720 = await client.ReadMAlignedWordAsync(720, 1, linked.Token); // M720 bit0, M730 bit10
            ushort raw720 = (ushort)(r720.IntValues.Length > 0 ? r720.IntValues[0] : 0);

            bool m730 = (raw720 & (1 << 10)) != 0;

            int d200Length = 0;
            try
            {
                var d = await client.ReadAsync(MitsubishiMcClient.DeviceD, 200, 1, linked.Token);
                d200Length = d.IntValues.Length > 0 ? d.IntValues[0] : 0;
            }
            catch { }

            if (cards.TryGetValue("ST709", out var c709))
            {
                c709.ConnectedBrush = Brushes.LimeGreen;
                c709.Status1 = m730 ? "可取板" : "末位无版";
                c709.Status1Brush = m730 ? Brushes.Orange : Brushes.Green;
                c709.Status2 = $"M730 D200长度={d200Length}";
                c709.IpText = "192.168.2.65:9000";
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            MarkMcCardReadFailed(cards, "ST709", "MC65超时");
        }
        catch (Exception ex)
        {
            MarkMcCardReadFailed(cards, "ST709", ex.Message);
        }
    }

    private async Task RefreshGrindingUnloadRackCardAsync(Dictionary<string, StationCardViewModel> cards, CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(2000);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var client = await _mcCache.GetOrCreateAsync("192.168.2.64", 9000, linked.Token);

            var result = await client.ReadMAlignedWordAsync(720, 1, linked.Token);
            ushort raw = (ushort)(result.IntValues.Length > 0 ? result.IntValues[0] : 0);
            bool m720CanPlace = (raw & 1) != 0;
            bool m730Safe = (raw & (1 << 10)) != 0;
            bool unloadReady = m720CanPlace && m730Safe;

            int unloadDiameter = 0;
            try
            {
                var d = await client.ReadAsync(MitsubishiMcClient.DeviceD, 200, 1, linked.Token);
                unloadDiameter = d.IntValues.Length > 0 ? d.IntValues[0] : 0;
            }
            catch { }

            if (cards.TryGetValue("ST710", out var c710))
            {
                c710.ConnectedBrush = Brushes.LimeGreen;
                c710.Status1 = unloadReady ? "可放料" : (!m720CanPlace ? "不可放料" : "未到安全位");
                c710.Status1Brush = unloadReady ? Brushes.Green : Brushes.Orange;
                c710.Status2 = $"M720={(m720CanPlace ? 1 : 0)} M730={(m730Safe ? 1 : 0)} D200={unloadDiameter}";
                c710.IpText = "192.168.2.64:9000";
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            MarkMcCardReadFailed(cards, "ST710", "MC64超时");
        }
        catch (Exception ex)
        {
            MarkMcCardReadFailed(cards, "ST710", ex.Message);
        }
    }

    private static void MarkMcCardReadFailed(Dictionary<string, StationCardViewModel> cards, string code, string reason)
    {
        if (!cards.TryGetValue(code, out var card)) return;
        card.ConnectedBrush = Brushes.Red;
        card.Status1 = "读取失败";
        card.Status1Brush = Brushes.Red;
        card.Status2 = reason.Length > 24 ? reason[..24] : reason;
    }

    private bool _isLine1Running;
    /// <summary>1号线是否在运行</summary>
    public bool IsLine1Running { get => _isLine1Running; set { if (SetField(ref _isLine1Running, value)) OnPropertyChanged(nameof(Line1ToggleText)); } }

    /// <summary>1号线启动/暂停按钮文本</summary>
    public string Line1ToggleText => _isLine1Running ? "暂停1号线" : "启动1号线";

    /// <summary>1号线缓存工件数量</summary>
    public int Line1CachedCount => _line1Engine?.CachedCount ?? 0;

    // ── 2号线运行状态 ────────────────────────────────────────────────
    private bool _isLine2Running;
    public bool IsLine2Running { get => _isLine2Running; set { if (SetField(ref _isLine2Running, value)) OnPropertyChanged(nameof(Line2ToggleText)); } }
    public string Line2ToggleText => _isLine2Running ? "暂停2号线" : "启动2号线";
    public int Line2CachedCount => _line2Engine?.CachedCount ?? 0;

    // ── 动平衡引擎 ────────────────────────────────────────────────
    private bool _isBalancingRunning;
    public bool IsBalancingRunning { get => _isBalancingRunning; set { if (SetField(ref _isBalancingRunning, value)) OnPropertyChanged(nameof(BalancingToggleText)); } }
    public string BalancingToggleText => _isBalancingRunning ? "暂停动平衡" : "启动动平衡";

    /// <summary>默认线路(1或2), 线路选取按钮切换</summary>
    private int _defaultRouteLine = 1;
    /// <summary>供View层设置默认线路</summary>
    public int DefaultRouteLine { get => _defaultRouteLine; set { _defaultRouteLine = value; Console.WriteLine($"[HomeViewModel] 线路选取→{value}号线"); } }

    /// <summary>缓存工件直径（mm）</summary>
    private int _cachedDiameter = 205;
    public int CachedDiameter { get => _cachedDiameter; set => SetField(ref _cachedDiameter, value); }

    /// <summary>缓存工件版孔（1=大孔 2=小孔）</summary>
    private int _cachedBoreType = 1;
    public int CachedBoreType { get => _cachedBoreType; set => SetField(ref _cachedBoreType, value); }

    /// <summary>缓存工件长度（mm）</summary>
    private int _cachedLength = 650;
    public int CachedLength { get => _cachedLength; set => SetField(ref _cachedLength, value); }

    /// <summary>测试动平衡: 直径(mm)</summary>
    private int _balancingTestDiameter = 147;
    public int BalancingTestDiameter { get => _balancingTestDiameter; set { if (SetField(ref _balancingTestDiameter, value)) OnPropertyChanged(); } }

    /// <summary>测试动平衡: 版长(mm)</summary>
    private int _balancingTestLength = 700;
    public int BalancingTestLength { get => _balancingTestLength; set { if (SetField(ref _balancingTestLength, value)) OnPropertyChanged(); } }

    /// <summary>测试动平衡: 可选位置列表</summary>
    public List<string> BalancingPositions { get; } = new() { "M817", "M818", "M821" };

    /// <summary>测试动平衡: 当前选中的位置</summary>
    private string _selectedBalancingPosition = "M817";
    public string SelectedBalancingPosition { get => _selectedBalancingPosition; set => SetField(ref _selectedBalancingPosition, value!); }

    /// <summary>启动/暂停研磨流程</summary>
    public ICommand GrindingToggleCommand { get; }

    /// <summary>启动/暂停1号线</summary>
    private async Task Line1ToggleAsync()
    {
        if (_line1Engine == null)
        {
            Console.WriteLine("[HomeViewModel] ✘ 1号线引擎未初始化"); return;
        }

        if (_isLine1Running)
        {
            Console.WriteLine("[HomeViewModel] ▶ 【暂停1号线】"); 
            _line1Engine.Pause();
            _line1RearEngine?.Pause();
            IsLine1Running = false;
        }
        else
        {
            if (_line1Engine.IsRunning || _line1RearEngine?.IsRunning == true)
            {
                Console.WriteLine("[HomeViewModel] ▶ 【恢复1号线】→ 前端+后端(动平衡单独恢复)");
                if (_line1Engine.IsRunning) _line1Engine.Resume(); else _line1Engine.Start();
                if (_line1RearEngine?.IsRunning == true) _line1RearEngine.Resume(); else _line1RearEngine?.Start();
            }
            else
            {
                Console.WriteLine("[HomeViewModel] ▶ 【启动1号线】→ 前端+后端(动平衡单独启动)");
                _line1Engine.Start();
                _line1RearEngine?.Start();
            }
            IsLine1Running = true;
        }
    }

    /// <summary>启动/暂停2号线(平衡引擎共用1号线的, 不重复启停)</summary>
    private async Task Line2ToggleAsync()
    {
        if (_line2Engine == null) { Console.WriteLine("[HomeViewModel] ✘ 2号线引擎未初始化"); return; }

        if (_isLine2Running)
        {
            Console.WriteLine("[HomeViewModel] ▶ 【暂停2号线】");
            _line2Engine.Pause();
            _line2RearEngine?.Pause();
            IsLine2Running = false;
        }
        else
        {
            if (_line2Engine.IsRunning || _line2RearEngine?.IsRunning == true)
            {
                Console.WriteLine("[HomeViewModel] ▶ 【恢复2号线】→ 前端+后端");
                if (_line2Engine.IsRunning) _line2Engine.Resume(); else _line2Engine.Start();
                if (_line2RearEngine?.IsRunning == true) _line2RearEngine.Resume(); else _line2RearEngine?.Start();
            }
            else
            {
                Console.WriteLine("[HomeViewModel] ▶ 【启动2号线】→ 前端+后端");
                _line2Engine.Start();
                _line2RearEngine?.Start();
            }
            IsLine2Running = true;
        }
    }

    /// <summary>启动/暂停动平衡流转(两条线共用)</summary>
    private async Task BalancingToggleAsync()
    {
        if (_line1BalancingEngine == null) { Console.WriteLine("[HomeViewModel] ✘ 动平衡引擎未初始化"); return; }

        if (_isBalancingRunning)
        {
            Console.WriteLine("[HomeViewModel] ▶ 【暂停动平衡】");
            _line1BalancingEngine.Pause();
            IsBalancingRunning = false;
        }
        else
        {
            if (_line1BalancingEngine.IsRunning && _line1BalancingEngine.IsPaused)
            {
                Console.WriteLine("[HomeViewModel] ▶ 【恢复动平衡】");
                _line1BalancingEngine.Resume();
            }
            else
            {
                Console.WriteLine("[HomeViewModel] ▶ 【启动动平衡】");
                _line1BalancingEngine.Start();
            }
            IsBalancingRunning = true;
        }
        await Task.CompletedTask;
    }

    public ICommand Line1ToggleCommand { get; }
    /// <summary>2号线启动/暂停</summary>
    public ICommand Line2ToggleCommand { get; }
    /// <summary>动平衡流转启动/暂停</summary>
    public ICommand BalancingToggleCommand { get; }
    /// <summary>将工件写入缓存</summary>
    public ICommand WriteCacheCommand { get; }
    /// <summary>清空工件缓存</summary>
    public ICommand ClearCacheCommand { get; }
    public ICommand TestRearPickupCommand { get; }
    public ICommand TestRearPickup2Command { get; }
    /// <summary>测试动平衡: 手动注入工件到M817/M818/M821缓存</summary>
    public ICommand TestBalancingRackPlacedCommand { get; }
    /// <summary>启动/停止ERP任务文件监听。只导入到任务列表, 不自动启动任务。</summary>
    public ICommand ErpTaskImportToggleCommand { get; }

    /// <summary>
    /// 主页面任务表数据源（左侧DataGrid绑定）。
    /// </summary>
    public ObservableCollection<TaskRowViewModel> TaskRows { get; } = new();

    private bool IsAnyProductionEngineRunning()
    {
        return _line1Engine?.IsRunning == true
            || _line1RearEngine?.IsRunning == true
            || _line2Engine?.IsRunning == true
            || _line2RearEngine?.IsRunning == true
            || _line1BalancingEngine?.IsRunning == true
            || _grindingEngine?.IsRunning == true;
    }

    private Task RefreshLoadedHomeAsync()
    {
        IsLoading = false;
        SyncBalancingCards(StationCards);
        OnPropertyChanged(nameof(IpMap));
        OnPropertyChanged(nameof(StationCards));
        OnPropertyChanged(nameof(Grinder1));
        OnPropertyChanged(nameof(Grinder2));
        OnPropertyChanged(nameof(Grinder3));
        OnPropertyChanged(nameof(Grinder4));
        OnPropertyChanged(nameof(Line1CachedCount));
        OnPropertyChanged(nameof(Line2CachedCount));
        OnPropertyChanged(nameof(GrindingCachedCount));
        Console.WriteLine("[HomeViewModel] LoadAsync轻量刷新: 引擎已初始化, 保留现有引擎/锁/缓存, 不重新连接、不重建流程实例");
        if (_cfg.ErpTaskImport.EnabledOnStartup && !IsErpTaskImportRunning)
            StartErpTaskImport();
        return Task.CompletedTask;
    }

    private void StopStatusSyncLoops()
    {
        _line1StatusCts?.Cancel();
        _line2StatusCts?.Cancel();
        _grindingRackStatusCts?.Cancel();
        _line1StatusCts = null;
        _line2StatusCts = null;
        _grindingRackStatusCts = null;
    }

    private async Task<bool> CanRebuildProductionContextAsync()
    {
        var reasons = new List<string>();

        if (IsAnyProductionEngineRunning())
            reasons.Add("生产引擎后台任务仍在运行/暂停中");
        if ((_line1Engine?.CachedCount ?? 0) > 0)
            reasons.Add($"1号线前端缓存={_line1Engine!.CachedCount}");
        if ((_line2Engine?.CachedCount ?? 0) > 0)
            reasons.Add($"2号线前端缓存={_line2Engine!.CachedCount}");
        if ((_line1RearEngine?.CachedCount ?? 0) > 0)
            reasons.Add($"1号线后端中转缓存={_line1RearEngine!.CachedCount}");
        if ((_line2RearEngine?.CachedCount ?? 0) > 0)
            reasons.Add($"2号线后端中转缓存={_line2RearEngine!.CachedCount}");
        if ((_line1BalancingEngine?.CachedCount ?? 0) > 0)
            reasons.Add($"动平衡缓存={_line1BalancingEngine!.CachedCount} Keys=[{_line1BalancingEngine.CacheKeysText}]");
        if ((_grindingEngine?.CachedCount ?? 0) > 0)
            reasons.Add($"研磨缓存={_grindingEngine!.CachedCount}");

        try
        {
            using var cts = new CancellationTokenSource(3000);
            var mc63 = await _mcCache.GetOrCreateAsync("192.168.2.63", 9000, cts.Token);
            var r63 = await mc63.ReadMAlignedWordAsync(800, 2, cts.Token);
            int m816Word = r63.IntValues.Length > 1 ? r63.IntValues[1] : 0;
            bool m817 = (m816Word & (1 << 1)) != 0;
            bool m818 = (m816Word & (1 << 2)) != 0;
            bool m821 = (m816Word & (1 << 5)) != 0;
            if (m817) reasons.Add("PLC现场M817=1(1号线动平衡下料架有板)");
            if (m818) reasons.Add("PLC现场M818=1(2号线动平衡下料架有板)");
            if (m821) reasons.Add("PLC现场M821=1(2号线短板中转位有板)");

            var mc65 = await _mcCache.GetOrCreateAsync("192.168.2.65", 9000, cts.Token);
            var r720 = await mc65.ReadMAlignedWordAsync(720, 1, cts.Token);
            bool m720CanPlace = (r720.IntValues[0] & 1) != 0;
            if (!m720CanPlace)
                reasons.Add("PLC现场M720=0(研磨上料1号位不可放料, 可能已有板或未到位)");
        }
        catch (Exception ex)
        {
            reasons.Add($"PLC现场状态读取失败: {ex.Message}");
        }

        if (reasons.Count == 0) return true;

        Console.WriteLine("[HomeViewModel] ⚠ 禁止强制重载流程引擎: " + string.Join("; ", reasons));
        return false;
    }

    /*异步获取机械手 天车的ip地址 然后存缓存中*/
    public async Task LoadAsync(bool forceReload = false)
    {
        await _loadLock.WaitAsync();
        try
        {
            if (_enginesInitialized && !forceReload)
            {
                await RefreshLoadedHomeAsync();
                return;
            }

            if (_enginesInitialized)
            {
                if (!await CanRebuildProductionContextAsync())
                {
                    IsLoading = false;
                    return;
                }

                StopStatusSyncLoops();
            }

            if (forceReload && IsAnyProductionEngineRunning())
            {
                // LoadAsync会刷新设备IP映射。生产运行中重载映射可能断开共享天车/机械手连接,
                // 因此直接拒绝, 保留当前连接和引擎上下文, 不影响正在执行的工件。
                Console.WriteLine("[HomeViewModel] ⚠ 生产引擎运行中, 禁止重新加载设备IP/连接映射; 请先暂停/停止产线后再刷新页面数据");
                return;
            }

        IsLoading = true;
        Console.WriteLine(forceReload
            ? "[HomeViewModel] ========== LoadAsync 强制重载开始 =========="
            : "[HomeViewModel] ========== LoadAsync 首次初始化开始 ==========");

        var machineRows = await _queryService.GetMachineRowsAsync();
        Console.WriteLine($"[HomeViewModel] 数据库查询完成：machine表 {machineRows.Count} 行");

        IpMap.Clear();
        foreach (var row in machineRows.Where(x => !string.IsNullOrWhiteSpace(x.StationCode)))
        {
            var key = row.StationCode.Trim().ToUpperInvariant();
            IpMap[key] = string.IsNullOrWhiteSpace(row.Ip) ? "未配置IP" : row.Ip.Trim();
        }

        Console.WriteLine("========== IpMap 内容（机械 IP）==========");
        foreach (var kvp in IpMap)
            Console.WriteLine($"  工位: {kvp.Key}  →  IP: {kvp.Value}");

        _craneCache.LoadFromMachineRows(machineRows);
        _manipulatorCache.LoadFromMachineRows(machineRows);
        ManualControl.NotifyMappingsUpdated();

        // ── 装载工位坐标到引擎 ──────────────────────────────────────
        _flowEngine.LoadStationCoords(machineRows);
        Console.WriteLine("[HomeViewModel] 引擎已装载工位坐标缓存");

        // ── 创建研磨流程引擎 ─────────────────────────────────────────
        var grindingCoords = machineRows
            .Where(r => !string.IsNullOrWhiteSpace(r.StationCode))
            .ToDictionary(r => r.StationCode.Trim().ToUpperInvariant(), r => r, StringComparer.OrdinalIgnoreCase);
        _stationDict = grindingCoords; // 缓存供弹窗查询
        _grindingEngine = new GrindingFlowEngine(_craneCache, _cfg, grindingCoords, _mcCache);
        Console.WriteLine($"[HomeViewModel] 研磨流程引擎已创建（{grindingCoords.Count} 个工位坐标）");

        // ── 前后端共享中转架互斥锁 ──
        _sharedTransferRackLock = new SemaphoreSlim(1, 1);
        // ── 机械手1只有一台, 1/2号线前端必须共用同一把锁, 防止两条线同时派机械手1 ──
        _sharedManipulatorLock = new SemaphoreSlim(1, 1);
        var sharedSafety = new SafetyFlags();

        // ── 平衡料架位置锁(4把): 保护天车和机械手同时操作同一位置, 防止碰撞 ──
        //    每把锁只保护一个信号地址: M817/M818/M821/M720
        //    持锁范围: 进入位置→放料/取料完成→离开位置后释放
        var lockM817 = new SemaphoreSlim(1, 1);
        var lockM818 = new SemaphoreSlim(1, 1);
        var lockM821 = new SemaphoreSlim(1, 1);
        var lockM720 = new SemaphoreSlim(1, 1);

        // ── 创建共享MC服务给前端引擎(避免1/2号线重复直连63) ──
        //     frontRackSvc 始终传给两个前端；即使首连失败，也只能通过_McCache重连，不能退化为各自new TCP。
        var frontRackSvc = new CenteringRackService("总上料架+中转架", _mcCache, "192.168.2.63", 9000);
        bool sharedMc63Ready = false;
        try
        {
            using var cts = new CancellationTokenSource(5000);
            await frontRackSvc.ConnectAsync(cts.Token);
            sharedMc63Ready = true;
            Console.WriteLine("[HomeViewModel] MC 192.168.2.63:9000 共享连接成功 ✓");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HomeViewModel] ⚠ MC 192.168.2.63 连接失败(5s超时): {ex.Message}");
            Console.WriteLine("[HomeViewModel]   前端引擎后续仍通过共享MC缓存重连, 禁止1/2号线各自直连");
        }
        _line1Engine = new Line1FrontFlowEngine(_craneCache, _manipulatorCache, _cfg, grindingCoords,
            _sharedTransferRackLock, sharedSafety, rackSvc: frontRackSvc,
            manipulatorLock: _sharedManipulatorLock);
        Console.WriteLine("[HomeViewModel] 1号线前端流程引擎已创建" + (sharedMc63Ready ? "(共享MC63已连接)" : "(共享MC63待重连)"));

        // ── 创建1号线后端流程引擎 (中转架状态从前端DeviceStatus读取) ──
        // M817+M720 锁传给1号线后端: DoUnload长工件→M817, 短工件→M720
        _line1RearEngine = new Line1RearFlowEngine(_craneCache, _manipulatorCache, _mcCache, _cfg, grindingCoords,
            _sharedTransferRackLock, sharedSafety, _line1Engine.DeviceStatus,
            lockM817: lockM817, lockM720: lockM720);
        Console.WriteLine("[HomeViewModel] 1号线后端流程引擎已创建");

        // ── 创建机械手2动平衡流转引擎 ──
        // 4把锁全传: M2Flow用M817或M818, M3Flow用M821+M720
        _line1BalancingEngine = new BalancingFlowEngine(_manipulatorCache, _craneCache, _mcCache, _cfg, grindingCoords,
            lockM817, lockM818, lockM821, lockM720);
        Console.WriteLine("[HomeViewModel] 机械手2动平衡流转引擎已创建(两条线共用)");

        // ── 创建2号线流程引擎 ──────────────────────────────────────
        _sharedTransferRackLock2 = new SemaphoreSlim(1, 1);
        var sharedSafety2 = new SafetyFlags();

        _line2Engine = new Line2FrontFlowEngine(_craneCache, _manipulatorCache, _cfg, grindingCoords,
            transferRackLock: _sharedTransferRackLock2, safety: sharedSafety2,
            rackSvc: frontRackSvc, manipulatorLock: _sharedManipulatorLock);
        Console.WriteLine("[HomeViewModel] 2号线前端流程引擎已创建(共享机械手锁)");

        // M818+M821 锁传给2号线后端: DoUnload长工件→M818, 短工件→M821
        _line2RearEngine = new Line2RearFlowEngine(_craneCache, _manipulatorCache, _mcCache, _cfg, grindingCoords,
            _sharedTransferRackLock2, sharedSafety2, _line2Engine.DeviceStatus,
            lockM818: lockM818, lockM821: lockM821);
        Console.WriteLine("[HomeViewModel] 2号线后端流程引擎已创建");

        // ── 前后端联动: 前端放中转架→通知后端工件数据 ──
        _line1Engine.OnRackPlaced = (code, wp) =>
        {
            // 前天车已物理放到中转架后, 后端缓存必须写入成功; 这里不能用?.静默跳过。
            if (_line1RearEngine == null) throw new InvalidOperationException("1号线中转架缓存回调失败: 后端引擎未创建");
            _line1RearEngine.SetRackWorkpiece(code, wp);
        };
        _line2Engine.OnRackPlaced = (code, wp) =>
        {
            // 前天车已物理放到中转架后, 后端缓存必须写入成功; 这里不能用?.静默跳过。
            if (_line2RearEngine == null) throw new InvalidOperationException("2号线中转架缓存回调失败: 后端引擎未创建");
            _line2RearEngine.SetRackWorkpiece(code, wp);
        };

        // ── 下料联动: 后天车放动平衡/下料架→通知平衡引擎(只有一台, 两条线共用) ──
        _line1RearEngine!.OnBalancingRackPlaced = (reg, wp) =>
        {
            // 后天车已物理放到动平衡/中转架后, 平衡缓存必须写入成功; 这里不能用?.静默跳过。
            if (_line1BalancingEngine == null) throw new InvalidOperationException("动平衡缓存回调失败: 平衡引擎未创建");
            _line1BalancingEngine.SetBalancingWp(reg, wp);
        };
        _line2RearEngine!.OnBalancingRackPlaced = (reg, wp) =>
        {
            // 后天车已物理放到动平衡/中转架后, 平衡缓存必须写入成功; 这里不能用?.静默跳过。
            if (_line1BalancingEngine == null) throw new InvalidOperationException("动平衡缓存回调失败: 平衡引擎未创建");
            _line1BalancingEngine.SetBalancingWp(reg, wp);
        };

        // ── 研磨联动: 后天车/M3Flow放研磨上料架ST010→通知研磨引擎入FIFO缓存 ──
        _line1RearEngine!.OnGrindingRackPlaced = wp =>
        {
            // 工件已物理放到研磨上料架后, 研磨FIFO必须写入成功; 这里不能用?.静默跳过。
            if (_grindingEngine == null) throw new InvalidOperationException("研磨缓存回调失败: 研磨引擎未创建");
            _grindingEngine.EnqueueWorkpiece(wp);
        };
        _line1BalancingEngine!.OnGrindingRackPlaced = wp =>
        {
            // 工件已物理放到研磨上料架后, 研磨FIFO必须写入成功; 这里不能用?.静默跳过。
            if (_grindingEngine == null) throw new InvalidOperationException("研磨缓存回调失败: 研磨引擎未创建");
            _grindingEngine.EnqueueWorkpiece(wp);
        };
        Console.WriteLine("[HomeViewModel] 研磨上料联动已绑定(1号线后天车+M3Flow→研磨缓存)");

        // ── 启动1号线状态同步（引擎→UI卡片，不另建连接，500ms刷新）───
        _line1StatusCts = new CancellationTokenSource();
        _ = SyncLine1StatusToCardsAsync(_line1StatusCts.Token);
        _line2StatusCts = new CancellationTokenSource();
        _ = SyncLine2StatusToCardsAsync(_line2StatusCts.Token);
        _grindingRackStatusCts = new CancellationTokenSource();
        _ = SyncGrindingRackStatusToCardsAsync(_grindingRackStatusCts.Token);

        // ── 初始化全厂状态卡片 ──────────────────────────────────────
        Console.WriteLine("[HomeViewModel] 开始初始化全厂状态总览卡片...");
        var newCards = new Dictionary<string, StationCardViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in machineRows)
        {
            if (string.IsNullOrWhiteSpace(row.StationCode)) continue;
            var code = row.StationCode.Trim().ToUpperInvariant();
            var typeName = row.TypeName?.Trim() ?? string.Empty;
            if (typeName == "天车" || typeName == "机械手") continue;
            bool isGrinder = code is "ST701" or "ST702" or "ST703" or "ST704";
            bool isFanuc = code is "ST112" or "ST606" or "ST607" or "ST608" or "ST609" or "ST610";
            var rawIp = string.IsNullOrWhiteSpace(row.Ip) ? "未配置IP" : row.Ip.Trim();
            // MC协议设备(端口9000): 192.168.2.63/64/65/88/89
            bool isMc = rawIp is "192.168.2.63" or "192.168.2.64" or "192.168.2.65" or "192.168.2.88" or "192.168.2.89";
            var ip = isGrinder ? "未配置IP" : rawIp;
            var port = isFanuc ? 8193 : (isMc ? 9000 : (row.Port > 0 ? row.Port : 502));
            // MC设备: 指定探测的M寄存器地址
            int mcReg = code switch { "ST019" => 817, "ST020" => 818, "ST021" => 821, _ => 0 };
            var card = new StationCardViewModel(code, string.IsNullOrWhiteSpace(row.Name) ? code : row.Name.Trim(),
                ip, port, typeName.Length > 0 ? typeName : "ModbusTCP", mcReg);
            newCards[code] = card;
            if (isGrinder) card.IpText = rawIp;
        }
        // ── 补全 XAML 中所有显示码，确保每个StatusCard绑定都有条目 ──
        void EnsureNewCard(string code, string name, string ip = "引擎托管", int port = 0)
        { if (!newCards.ContainsKey(code)) { newCards[code] = new StationCardViewModel(code, name, ip, port, "引擎托管"); Console.WriteLine($"[HomeViewModel] +预建卡片 {code} {name}"); } }
        // 1号线前端 (映射DB或纯显示码)
        EnsureNewCard("ST001", "总上料架");        // → ST007 MC:192.168.2.63
        EnsureNewCard("ST002", "机械手1");          // → ST002 Modbus:192.168.2.85 (引擎托管)
        EnsureNewCard("ST011", "货叉1");            // → ST711 MC:192.168.2.88
        EnsureNewCard("ST401", "一号双头镗");       // → ST401 Syntec:192.168.2.66 (引擎托管)
        EnsureNewCard("ST501", "一号打号机");       // → ST501 文件:D:\1 (引擎托管)
        EnsureNewCard("ST901", "1号线天车前");      // → ST901 Modbus:192.168.2.81 (引擎托管)
        EnsureNewCard("ST902", "1号线天车后");      // → 2号天车 Modbus:192.168.2.82 (引擎托管)
        EnsureNewCard("ST105", "1号线中转架1");     // M811 192.168.2.63
        EnsureNewCard("ST101", "1号线中转架2");     // M812 192.168.2.63
        EnsureNewCard("ST106", "1号线中转架3");     // M813 192.168.2.63
        // 2号线前端/中转
        EnsureNewCard("ST712", "货叉2");            // → ST712 MC:192.168.2.89
        EnsureNewCard("ST903", "2号线天车前");      // → 3号天车 Modbus:192.168.2.83
        EnsureNewCard("ST904", "2号线天车后");      // → 4号天车 Modbus:192.168.2.84
        EnsureNewCard("ST016", "2号线中转架1");     // M814 192.168.2.63
        EnsureNewCard("ST017", "2号线中转架2");     // M815 192.168.2.63
        EnsureNewCard("ST018", "2号线中转架3");     // M816 192.168.2.63
        // 下料架 / 动平衡 / 研磨上料架 (MC设备)
        EnsureNewCard("ST019", "1号线动平衡下料架1"); // M817 192.168.2.63
        EnsureNewCard("ST020", "2号线动平衡下料架1"); // M818 192.168.2.63
        EnsureNewCard("ST021", "2号线短板中转位");   // M821 192.168.2.63
        EnsureNewCard("ST008", "动平衡下料架1");    // M710 192.168.2.65
        EnsureNewCard("ST009", "动平衡下料架2");    // M700 192.168.2.65
        EnsureNewCard("ST010", "研磨上料1号位");    // M720 192.168.2.65
        EnsureNewCard("ST709", "研磨机上料架");     // → ST709 MC65: M730末位有板/D200测长
        EnsureNewCard("ST710", "研磨机下料架");     // → ST710 MC64: M720/M730
        // 机械手2/3 (Modbus, 引擎托管)
        EnsureNewCard("ST005", "机械手2");          // → ST005 Modbus:192.168.2.86
        EnsureNewCard("ST006", "机械手3");          // → ST006 Modbus:192.168.2.87
        // 斜床 ST601~ST605 在DB中有记录 → 已包含
        // 研磨机 ST701~ST704 在DB中有记录 → 已包含
        StationCards = newCards;

        
        // ── 创建研磨机卡片（IP从IpMap缓存取，不启动连接）───────────
        Grinder1 = CreateGrinderCard("ST701", "研磨机1(新代)",   PlcGrinderService.GrinderType.TypeB);
        Grinder2 = CreateGrinderCard("ST702", "研磨机2(新代)",   PlcGrinderService.GrinderType.TypeB);
        Grinder3 = CreateGrinderCard("ST703", "研磨机3(西门子)", PlcGrinderService.GrinderType.TypeA);
        Grinder4 = CreateGrinderCard("ST704", "研磨机4(西门子)", PlcGrinderService.GrinderType.TypeA);
        OnPropertyChanged(nameof(Grinder1)); OnPropertyChanged(nameof(Grinder2));
        OnPropertyChanged(nameof(Grinder3)); OnPropertyChanged(nameof(Grinder4));

        // ═══════════════════════════════════════════════════════════
        //  缓存全部就绪，关闭加载遮罩。连接放后台不阻塞。
        // ═══════════════════════════════════════════════════════════
        IsLoading = false;
        OnPropertyChanged(nameof(IpMap));
        Console.WriteLine("[HomeViewModel] 缓存全部就绪，页面已显示。后台开始连接...");

        // ── 一次性探测：MC 设备 / Syntec 双头镗 — 连→读→写卡片→断开 ──
        _ = ProbeMcDevicesAsync();
        _ = ProbeSyntecDevicesAsync();

        
        _ = Task.Run(async () =>
        {
            await Task.WhenAll(
                SafeStartAsync(Crane1F), SafeStartAsync(Crane1R),
                SafeStartAsync(Crane2F), SafeStartAsync(Crane2R),
                SafeStartAsync(CraneGL));
            Console.WriteLine("[HomeViewModel] 5台天车后台连接完成");
        });
        _ = Task.Run(async () =>
        {
            await Task.WhenAll(
                SafeStartManipulatorAsync(Manipulator1),
                SafeStartManipulatorAsync(Manipulator2),
                SafeStartManipulatorAsync(Manipulator3));
            Console.WriteLine("[HomeViewModel] 3台机械手后台连接完成");
        });
        // 跳过引擎已管理的设备 以及 非ModbusTCP协议设备
        // ST605=沈阳FANUC FOCAS无法走ModbusTCP探测; ST401/Syntec已单独探测; ST501/文件握手无需TCP
        var engineManagedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ST007", "ST711", "ST103", "ST107", "ST901", "ST002", "ST401", "ST501", "ST605" };
        _ = Task.Run(async () =>
        {
            var tasks = newCards.Values.Where(c => c.HasIp && !engineManagedCodes.Contains(c.StationCode)).Select(c => SafeStartStationAsync(c)).ToList();
            if (tasks.Count > 0) await Task.WhenAll(tasks);
            Console.WriteLine($"[HomeViewModel] {tasks.Count} 个站卡后台连接完成（已跳过引擎托管设备）");
        });
        _ = Task.Run(async () =>
        {
            await StartGrinderConnectionsAsync(newCards);
            Console.WriteLine("[HomeViewModel] 研磨机GrinderPoll后台连接完成（已注入共享服务到卡片）");
        });

        _enginesInitialized = true;
        Console.WriteLine(forceReload
            ? "[HomeViewModel] ========== LoadAsync 强制重载完成 =========="
            : "[HomeViewModel] ========== LoadAsync 首次初始化完成 ==========");
        if (_cfg.ErpTaskImport.EnabledOnStartup && !IsErpTaskImportRunning)
            StartErpTaskImport();
        }
        finally
        {
            _loadLock.Release();
        }
    }
    
    private static async Task SafeStartAsync(CraneCardViewModel vm)
    {
        try
        {
            await vm.StartAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HomeViewModel] [{vm.CraneName}] StartAsync 异常：{ex.Message}");
        }
    }

    private static async Task SafeStartManipulatorAsync(ManipulatorCardViewModel vm)
    {
        try
        {
            await vm.StartAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HomeViewModel] [{vm.Name}] StartAsync 异常：{ex.Message}");
        }
    }

    /// <summary>从IpMap缓存取IP创建研磨机卡片（不启动连接）。</summary>
    private GrinderCardViewModel CreateGrinderCard(string stationCode, string name, PlcGrinderService.GrinderType type)
    {
        string ip = IpMap.TryGetValue(stationCode, out var v) && v != "未配置IP" ? v : "";
        Console.WriteLine($"[GrinderCard] {name} ({stationCode}) 从IpMap缓存取IP={(string.IsNullOrWhiteSpace(ip) ? "无" : ip)}");
        return new GrinderCardViewModel(name, ip, type);
    }


    /// <summary>安全启动站卡轮询，连接失败不抛异常。</summary>
    private static async Task SafeStartStationAsync(StationCardViewModel card)
    {
        try { await card.StartPollingAsync(); }
        catch (Exception ex) { Console.WriteLine($"[HomeViewModel] [{card.Title}] 站卡启动异常：{ex.Message}"); }
    }

    /// <summary>
    /// 自动连接4台研磨机（ST701/ST702=新代TypeB, ST703/ST704=西门子TypeA）。
    /// 有IP则用PlcGrinderService连，每5s读状态并更新对应站卡显示。
    /// </summary>
    private async Task StartGrinderConnectionsAsync(Dictionary<string, StationCardViewModel> cards)
    {
        var grinderDefs = new (string code, string name, PlcGrinderService.GrinderType type, GrinderCardViewModel grinderCard)[]
        {
            ("ST701", "研磨机1(新代)",   PlcGrinderService.GrinderType.TypeB, Grinder1),
            ("ST702", "研磨机2(新代)",   PlcGrinderService.GrinderType.TypeB, Grinder2),
            ("ST703", "研磨机3(西门子)", PlcGrinderService.GrinderType.TypeA, Grinder3),
            ("ST704", "研磨机4(西门子)", PlcGrinderService.GrinderType.TypeA, Grinder4),
        };

        foreach (var (code, name, gtype, grinderCard) in grinderDefs)
        {
            // 必须有站卡
            if (!cards.TryGetValue(code, out var card))
            {
                Console.WriteLine($"[GrinderPoll] {name} ({code}) 站卡不存在，跳过");
                continue;
            }

            // IP 从引擎的 station_coords 缓存读（数据库 machine 表）
            if (!_flowEngine.TryGetStationIp(code, out var ip))
            {
                Console.WriteLine($"[GrinderPoll] {name} ({code}) IP未配置（数据库无记录），跳过");
                continue;
            }

            card.IpText = $"{ip}:502";
            Console.WriteLine($"[GrinderPoll] {name} ({code}) 启动轮询 Type={gtype} IP={ip}");
            _ = GrinderPollLoopAsync(code, name, gtype, ip, card, grinderCard, _grindingEngine!);
        }
    }

    /// <summary>研磨机轮询循环：连接 → 5s读状态 → 更新站卡 → 断开重连。</summary>
    private static async Task GrinderPollLoopAsync(string code, string name,
        PlcGrinderService.GrinderType gtype, string ip, StationCardViewModel stationCard,
        GrinderCardViewModel grinderCard, GrindingFlowEngine grindingEngine)
    {
        PlcGrinderService? svc = null;
        int delay = 5000;
        int cycleCount = 0;

        Console.WriteLine($"[GrinderPoll] [{name}] ========== 轮询线程启动 IP={ip} ==========");

        while (true)
        {
            cycleCount++;
            try
            {
                // 首次或断连 → 重建连接
                if (svc == null || !svc.IsConnected)
                {
                    if (svc != null && !grindingEngine.CanReplaceGrinderService(code, out var holdReason))
                    {
                        // 研磨引擎正在上料/下料/等待下料时, 旧Svc可能仍被动作线程持有。
                        // 这里只标记UI通信异常, 不Dispose旧服务, 避免打断正在进行的PLC握手。
                        Console.WriteLine($"[GrinderPoll] [{name}] 连接已断但暂不重建: {holdReason}");
                        stationCard.Status1 = "连接异常";
                        stationCard.Status2 = "引擎动作中, 暂缓重连";
                        stationCard.ConnectedBrush = System.Windows.Media.Brushes.Gray;
                        stationCard.Status1Brush = System.Windows.Media.Brushes.Gray;
                        grinderCard.SetDisconnected();
                        await Task.Delay(delay);
                        delay = Math.Min(delay * 2, 30000);
                        continue;
                    }

                    Console.WriteLine($"[GrinderPoll] [{name}] {(svc == null ? "首次连接" : "重连")} IP={ip}...");
                    var oldSvc = svc;
                    var newSvc = new PlcGrinderService(name, ip, gtype);
                    await newSvc.ConnectAsync();

                    if (!grindingEngine.TrySetGrinderService(code, newSvc, out var rejectReason))
                    {
                        // 双保险: 连接过程中状态可能从Idle变成Loading/Unloading。
                        // 新服务尚未注入引擎, 可以安全释放; 旧服务不能释放, 仍由引擎继续持有。
                        Console.WriteLine($"[GrinderPoll] [{name}] 新连接已建立但未注入: {rejectReason}");
                        newSvc.Dispose();
                        svc = oldSvc;
                        stationCard.Status1 = "连接暂缓";
                        stationCard.Status2 = "引擎动作中";
                        stationCard.ConnectedBrush = System.Windows.Media.Brushes.Gray;
                        stationCard.Status1Brush = System.Windows.Media.Brushes.Gray;
                        grinderCard.SetDisconnected();
                        await Task.Delay(delay);
                        delay = Math.Min(delay * 2, 30000);
                        continue;
                    }

                    oldSvc?.Dispose();
                    svc = newSvc;
                    Console.WriteLine($"[GrinderPoll] [{name}] ✔ 连接成功 IP={ip} Type={gtype}");
                    delay = 5000;
                    stationCard.ConnectedBrush = System.Windows.Media.Brushes.LimeGreen;
                    // 注入共享服务到研磨机卡片 + 研磨流程引擎，避免各自另建连接导致双连接冲突
                    grinderCard.SetSharedService(svc);
                }

                // 读全部状态
                if (gtype == PlcGrinderService.GrinderType.TypeA)
                {
                    int di = await ReadGrinderDIAsync(svc, code);
                    bool fault   = (di & (1 << 0)) != 0;  // bit0=报警
                    bool busy    = (di & (1 << 14)) != 0; // bit14=加工中
                    bool reqData = (di & (1 << 9)) != 0;  // bit9=请求数据
                    bool stone1  = (di & (1 << 1)) != 0;  // bit1=磨石1报警
                    bool stone2  = (di & (1 << 2)) != 0;  // bit2=磨石2报警

                    stationCard.ConnectedBrush = fault ? System.Windows.Media.Brushes.Red
                        : System.Windows.Media.Brushes.LimeGreen;
                    stationCard.Status1Brush = fault ? System.Windows.Media.Brushes.Red
                        : System.Windows.Media.Brushes.Green;
                    stationCard.Status1 = fault ? "故障" : (busy ? "加工中" : (reqData ? "请求数据" : "空闲"));
                    stationCard.Status2 = fault ? $"DI=0x{di:X4}" : "西门子PLC";

                    // 同步更新研磨机卡片（共享同一 DI 读值）
                    grinderCard.UpdateTypeA(di);

                    Console.WriteLine($"[GrinderPoll] [{name}] #{cycleCount} TypeA DI=0x{di:X4} " +
                        $"报警={fault} 加工={busy} 请求数据={reqData} 磨石1={stone1} 磨石2={stone2}");
                }
                else
                {
                    int status   = await svc.GetMachineStatusAsync();
                    bool reqData = await svc.IsRequestDataAsync();
                    bool reqLoad = await svc.IsRequestLoadAsync();
                    bool clamped = await svc.IsClampDoneLoadOutAsync();
                    bool reqUnld = await svc.IsRequestUnloadAsync();
                    bool unclamp = await svc.IsUnclampDoneAsync();
                    bool busy    = await svc.IsMachiningAsync();
                    bool door    = await svc.IsDoorOpenAsync();

                    stationCard.ConnectedBrush = status == 2 ? System.Windows.Media.Brushes.Red
                        : System.Windows.Media.Brushes.LimeGreen;
                    stationCard.Status1Brush = status == 2 ? System.Windows.Media.Brushes.Red
                        : System.Windows.Media.Brushes.Green;
                    stationCard.Status1 = status == 2 ? "报警" : (busy ? "加工中" : (reqData ? "请求数据" : "空闲"));
                    stationCard.Status2 = status == 2 ? $"R7308={status}" : (door ? "安全门开" : "新代数控");

                    // 同步更新研磨机卡片
                    grinderCard.UpdateTypeB(status, reqData, reqLoad, clamped, reqUnld, unclamp, busy, door);

                    Console.WriteLine($"[GrinderPoll] [{name}] #{cycleCount} TypeB R7308={status} " +
                        $"加工={busy} 请求数据={reqData} 门开={door}");
                }

                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GrinderPoll] [{name}] #{cycleCount} ✘ 异常：{ex.GetType().Name} — {ex.Message}");
                stationCard.Status1 = "连接失败";
                stationCard.Status2 = "等待重连...";
                stationCard.ConnectedBrush = System.Windows.Media.Brushes.Gray;
                stationCard.Status1Brush = System.Windows.Media.Brushes.Gray;
                grinderCard.SetDisconnected();
                Console.WriteLine($"[GrinderPoll] [{name}] 退避 {delay / 1000}s 后重试...");
                await Task.Delay(delay);
                delay = Math.Min(delay * 2, 30000);
            }
        }
    }

    /// <summary>读研磨机 TypeA DI 寄存器（Modbus地址=0，即40001）。</summary>
    private static async Task<int> ReadGrinderDIAsync(PlcGrinderService svc, string code)
    {
        // 用反射或直接读——PlcGrinderService 没有公开读DI的方法，
        // 通过 ReadAllStatusAsync 的方式读数：读Modbus地址0
        try
        {
            return await svc.ReadDIRawAsync();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 接收弹窗新增任务，添加到 UI 列表并投入流程引擎队列。
    /// 引擎在线程池后台运行，不阻塞 UI。
    /// </summary>
    private void ReportTaskProgress(TaskRowViewModel row, string step, string state = "运行中")
    {
        try
        {
            void Apply()
            {
                row.Step = step;
                row.State = state;
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) Apply();
            else dispatcher.BeginInvoke((Action)Apply);
        }
        catch (Exception ex)
        {
            // 进度显示是旁路能力, 失败绝不能影响现场流程。
            Console.WriteLine($"[HomeViewModel] ⚠ 更新任务进度失败 版号={row.PlateNo} 序号={row.Sequence}: {ex.Message}");
        }
    }

    private Service.WorkpieceCache BuildWorkpieceCache(TaskRowViewModel taskRow)
    {
        bool skipBoring = taskRow.ProcessType == "省去双头镗工艺";
        return new Service.WorkpieceCache
        {
            PlateNo = taskRow.PlateNo,
            Sequence = taskRow.Sequence,
            Diameter = taskRow.Diameter,
            BoreType = (int)taskRow.PlugHole,  // 70=小孔, 100=大孔
            Length = taskRow.Length,
            MarkingContent = taskRow.MarkingContent,
            LeftPlugThickness = taskRow.LeftPlugThickness,
            RightPlugThickness = taskRow.RightPlugThickness,
            BoringProcess = taskRow.BoringProcess,
            SkewBedProcess = taskRow.SkewBedProcess,
            SkipBoring = skipBoring,
            ForceBalancing = taskRow.ForceBalancing,
            ReportProgress = (step, state) => ReportTaskProgress(taskRow, step, state)
        };
    }

    private void RejectTaskStart(TaskRowViewModel taskRow, string reason)
    {
        taskRow.RejectStart(reason);
        System.Windows.MessageBox.Show(reason, "任务启动失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }

    private bool IsLengthAllowedForTransferRackLine(double length, int line, out string message)
    {
        if (length > 1200 && length <= 1500)
        {
            message = line == 1 ? string.Empty : "长板(1200~1500)只能从1号线中转架开始";
            return line == 1;
        }

        if (length >= 400 && length <= 1200)
        {
            message = string.Empty;
            return line is 1 or 2;
        }

        message = "版长不在任何线路范围内";
        return false;
    }

    private bool TryGetManualRackHasPlate(int line, string rackCode, out bool hasPlate, out string message)
    {
        hasPlate = false;
        if (line == 1)
        {
            var ds = _line1Engine?.DeviceStatus;
            if (_line1Engine?.IsRunning != true || ds == null)
            {
                message = "1号线前端引擎未运行, 无法确认中转架有板信号";
                return false;
            }

            if (!ds.RackConnected)
            {
                message = "1号线中转架PLC未连接, 无法确认有板信号";
                return false;
            }

            hasPlate = rackCode switch
            {
                "ST105" => !ds.TransferRack1Free,
                "ST101" => !ds.TransferRack2Free,
                "ST106" => !ds.TransferRack3Free,
                _ => false
            };
        }
        else if (line == 2)
        {
            var ds = _line2Engine?.DeviceStatus;
            if (_line2Engine?.IsRunning != true || ds == null)
            {
                message = "2号线前端引擎未运行, 无法确认中转架有板信号";
                return false;
            }

            if (!ds.RackConnected)
            {
                message = "2号线中转架PLC未连接, 无法确认有板信号";
                return false;
            }

            hasPlate = rackCode switch
            {
                "ST016" => !ds.TransferRack4Free,
                "ST017" => !ds.TransferRack5Free,
                "ST018" => !ds.TransferRack6Free,
                _ => false
            };
        }
        else
        {
            message = "无效线号, 无法确认中转架有板信号";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private bool TryStartFromTransferRack(TaskRowViewModel taskRow, Service.WorkpieceCache wp)
    {
        string rackCode = taskRow.TransferRackCode.Trim().ToUpperInvariant();
        int line = taskRow.TransferRackLine;
        if (line is not (1 or 2) || string.IsNullOrWhiteSpace(rackCode))
        {
            RejectTaskStart(taskRow, "从中转架开始时必须选择有效的中转架");
            return false;
        }

        if (!IsLengthAllowedForTransferRackLine(taskRow.Length, line, out var lengthMessage))
        {
            RejectTaskStart(taskRow, lengthMessage);
            return false;
        }

        // 启动写缓存前先确认现场真实有板；这里只读前端刷新出来的PLC状态, 不写PLC有板信号。
        if (!TryGetManualRackHasPlate(line, rackCode, out var hasPlate, out var rackMessage))
        {
            RejectTaskStart(taskRow, rackMessage);
            return false;
        }

        if (!hasPlate)
        {
            RejectTaskStart(taskRow, $"{taskRow.TransferRackDisplayName} 当前无板信号, 请先人工放板后再启动");
            return false;
        }

        if (line == 1)
        {
            if (_line1RearEngine?.IsRunning != true)
            {
                RejectTaskStart(taskRow, "1号线后端引擎未运行, 不能写入中转架缓存");
                return false;
            }

            if (!_line1RearEngine.TrySetManualRackWorkpiece(rackCode, wp, out var message))
            {
                RejectTaskStart(taskRow, message);
                return false;
            }
        }
        else
        {
            if (_line2RearEngine?.IsRunning != true)
            {
                RejectTaskStart(taskRow, "2号线后端引擎未运行, 不能写入中转架缓存");
                return false;
            }

            if (!_line2RearEngine.TrySetManualRackWorkpiece(rackCode, wp, out var message))
            {
                RejectTaskStart(taskRow, message);
                return false;
            }
        }

        taskRow.AssignedLine = line;
        // 只写后端缓存, 不写PLC有板信号；后端仍需读到真实中转架有板信号后才会上料。
        wp.ReportStage($"人工上中转架 {rackCode}");
        Console.WriteLine($"[HomeViewModel] 📥 人工中转架任务写入缓存 {taskRow.TransferRackDisplayName} {wp.IdentityText} d={wp.Diameter} L={wp.Length}");
        return true;
    }

    private Task ErpTaskImportToggleAsync()
    {
        if (IsErpTaskImportRunning)
            StopErpTaskImport();
        else
            StartErpTaskImport();

        return Task.CompletedTask;
    }

    private void StartErpTaskImport()
    {
        if (IsErpTaskImportRunning) return;

        _erpTaskImportCts = new CancellationTokenSource();
        IsErpTaskImportRunning = true;
        SetErpTaskImportStatus($"ERP监听：监听中 {_cfg.ErpTaskImport.FilePath}");
        _erpTaskImportTask = Task.Run(() => ErpTaskImportLoopAsync(_erpTaskImportCts.Token));
        Console.WriteLine($"[ERP导入] ▶ 开始监听ERP任务文件: {_cfg.ErpTaskImport.FilePath}");
    }

    private void StopErpTaskImport()
    {
        if (!IsErpTaskImportRunning) return;

        _erpTaskImportCts?.Cancel();
        IsErpTaskImportRunning = false;
        SetErpTaskImportStatus("ERP监听：已停止");
        Console.WriteLine("[ERP导入] ⏸ 停止监听");
    }

    private async Task ErpTaskImportLoopAsync(CancellationToken ct)
    {
        int pollMs = Math.Max(500, _cfg.ErpTaskImport.PollIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollErpTaskFileOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERP导入] ⚠ 轮询异常: {ex.Message}");
                await SetErpTaskImportStatusAsync($"ERP监听：异常 {ex.Message}");
            }

            try { await Task.Delay(pollMs, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private async Task PollErpTaskFileOnceAsync(CancellationToken ct)
    {
        string path = _cfg.ErpTaskImport.FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            await SetErpTaskImportStatusAsync("ERP监听：文件路径未配置");
            return;
        }

        if (!File.Exists(path))
        {
            await SetErpTaskImportStatusAsync($"ERP监听：文件不存在 {path}");
            return;
        }

        string firstRead;
        try
        {
            firstRead = await ReadTextSharedAsync(path, ct);
        }
        catch (IOException ex)
        {
            await SetErpTaskImportStatusAsync($"ERP监听：文件占用 {ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(firstRead))
        {
            await SetErpTaskImportStatusAsync($"ERP监听：监听中 {path}");
            return;
        }

        // ERP写文件时可能先创建再写内容。这里短暂等待并复读一次,
        // 两次内容一致才解析；如果还在变化, 本轮不清空、不入队。
        await Task.Delay(200, ct);
        string secondRead = await ReadTextSharedAsync(path, ct);
        if (!string.Equals(firstRead, secondRead, StringComparison.Ordinal))
        {
            await SetErpTaskImportStatusAsync("ERP监听：检测到文件正在写入, 等待稳定");
            return;
        }

        string raw = secondRead.Trim();
        if (!TryGetSingleErpLine(raw, out string line, out string lineError))
        {
            await BackupBadErpTaskAsync(raw, lineError, ct);
            await ClearErpTaskFileAsync(path, ct);
            await SetErpTaskImportStatusAsync($"ERP监听：解析失败 {lineError}");
            return;
        }

        if (!ErpTaskImportParser.TryParse(line, out var row, out string parseError) || row == null)
        {
            await BackupBadErpTaskAsync(line, parseError, ct);
            await ClearErpTaskFileAsync(path, ct);
            await SetErpTaskImportStatusAsync($"ERP监听：解析失败 {parseError}");
            return;
        }

        // TaskRows是UI绑定集合, 必须回到WPF UI线程写入。
        // 这里只调用AddTask, 不启动任务, 后续仍由人工点击“启动”进入原业务流程。
        var importResult = await RunOnUiAsync(() =>
        {
            if (TaskRows.Any(x => string.Equals(x.PlateNo, row.PlateNo, StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(x.Sequence, row.Sequence, StringComparison.OrdinalIgnoreCase)))
            {
                return (Ok: false, Message: $"任务重复: 版号={row.PlateNo} 序号={row.Sequence}");
            }

            AddTask(row);
            return (Ok: true, Message: $"已导入 版号={row.PlateNo} 序号={row.Sequence}");
        });

        if (!importResult.Ok)
        {
            await BackupBadErpTaskAsync(line, importResult.Message, ct);
            await ClearErpTaskFileAsync(path, ct);
            await SetErpTaskImportStatusAsync($"ERP监听：导入失败 {importResult.Message}");
            return;
        }

        await ClearErpTaskFileAsync(path, ct);
        await SetErpTaskImportStatusAsync($"ERP监听：{importResult.Message}");
        Console.WriteLine($"[ERP导入] ✓ {importResult.Message}; 原始='{line}'");
    }

    private static bool TryGetSingleErpLine(string content, out string line, out string error)
    {
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1)
        {
            line = string.Empty;
            error = $"ERP文件必须只有一行任务, 当前非空行数={lines.Length}";
            return false;
        }

        line = lines[0];
        error = string.Empty;
        return true;
    }

    private async Task BackupBadErpTaskAsync(string raw, string reason, CancellationToken ct)
    {
        string dir = string.IsNullOrWhiteSpace(_cfg.ErpTaskImport.ErrorDirectory)
            ? @"D:\ErpTaskError"
            : _cfg.ErpTaskImport.ErrorDirectory;
        Directory.CreateDirectory(dir);

        string fileName = $"erp_bad_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt";
        string path = Path.Combine(dir, fileName);
        string text = $"原因: {reason}{Environment.NewLine}时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}原始内容:{Environment.NewLine}{raw}{Environment.NewLine}";
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);
        Console.WriteLine($"[ERP导入] ⚠ 任务解析/导入失败, 已备份: {path}; 原因={reason}");
    }

    private static async Task<string> ReadTextSharedAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await sr.ReadToEndAsync(ct);
    }

    private static async Task ClearErpTaskFileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        fs.SetLength(0);
        await fs.FlushAsync(ct);
        Console.WriteLine($"[ERP导入] 已清空ERP任务文件: {path}");
    }

    private async Task<T> RunOnUiAsync<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            return action();

        return await dispatcher.InvokeAsync(action);
    }

    private void SetErpTaskImportStatus(string status)
    {
        if (ErpTaskImportStatus == status) return;

        ErpTaskImportStatus = status;
        Console.WriteLine($"[ERP导入] 状态: {status}");
    }

    private async Task SetErpTaskImportStatusAsync(string status)
    {
        await RunOnUiAsync(() =>
        {
            SetErpTaskImportStatus(status);
            return true;
        });
    }

    public void AddTask(TaskRowViewModel row)
    {
        // 删除只处理尚未启动、尚未写入任何线路缓存的任务行。
        // 已启动任务的 WorkpieceCache 可能已进入引擎流程，不能仅删除 UI，避免现场仍在跑但页面看不到。
        row.OnDeleteRequested = DeleteTaskRow;

        // 设置启动回调：用户点任务行的「启动」→ 分配工件到对应线路
        row.OnStartRequested = taskRow =>
        {
            // 先构建完整工件数据；无论从总上料架还是人工中转架开始, 后续缓存都保存同一份WorkpieceCache。
            var wp = BuildWorkpieceCache(taskRow);
            if (taskRow.ProcessType == "省去双头镗工艺") Console.WriteLine($"[HomeViewModel] ⚡ 工件跳过双头镗工艺");

            if (taskRow.StartFromTransferRack)
            {
                TryStartFromTransferRack(taskRow, wp);
                return;
            }

            // ① 自动分配线路
            //    安全优先: 大直径工件强制1号线, 避免机械手1给1号线送料时与2号线货叉上的大板干涉。
            //    业务规则: 1200-1500强制1号线, 400-1200空闲优先否则按缓存数少的分。
            int line;
            string routeReason;
            int largeDiameterLine1OnlyMm = _cfg.SkewBed.LargeDiameterLine1OnlyMm;
            if (largeDiameterLine1OnlyMm > 0 && taskRow.Diameter >= largeDiameterLine1OnlyMm)
            {
                line = 1;
                routeReason = $"直径{taskRow.Diameter}mm>={largeDiameterLine1OnlyMm}mm, 强制1号线防止穿越2号线货叉区域大板干涉";
            }
            else if (taskRow.Length > 1200 && taskRow.Length <= 1500)
            {
                line = 1; // 长版只能1号线
                routeReason = "版长1200-1500mm, 只能1号线";
            }
            else if (taskRow.Length >= 400 && taskRow.Length <= 1200)
            {
                bool l1run = _line1Engine?.IsRunning == true;
                bool l2run = _line2Engine?.IsRunning == true;
                int l1cnt = _line1Engine?.CachedCount ?? 999;
                int l2cnt = _line2Engine?.CachedCount ?? 999;

                if (!l1run && !l2run) { line = _defaultRouteLine; routeReason = "两线未启动, 按页面默认线路"; }
                else if (!l1run)      { line = 2; routeReason = "只有2号线运行"; }
                else if (!l2run)      { line = 1; routeReason = "只有1号线运行"; }
                else                  { line = l1cnt <= l2cnt ? 1 : 2; routeReason = $"两线运行, 按缓存少优先(1号={l1cnt},2号={l2cnt})"; }
            }
            else { line = 0; routeReason = "版长不在任何线路范围内"; }

            taskRow.AssignedLine = line;
            Console.WriteLine($"[HomeViewModel] 工件分配 版号={taskRow.PlateNo} 序号={taskRow.Sequence} D={taskRow.Diameter} L={taskRow.Length} → {line}号线; 原因={routeReason} (1号线缓存={_line1Engine?.CachedCount} 2号线缓存={_line2Engine?.CachedCount})");

            if (line == 0)
            {
                Console.WriteLine("[HomeViewModel] ⚠ 版长不在任何线路范围内");
                RejectTaskStart(taskRow, "版长不在任何线路范围内");
                return;
            }

            // ③ 入队对应线路引擎
            if (line == 1) _line1Engine?.EnqueueWorkpiece(wp);
            else if (line == 2) _line2Engine?.EnqueueWorkpiece(wp);
            wp.ReportStage($"已入{line}号线缓存");
            Console.WriteLine($"[HomeViewModel] 📥 工件入{line}号线缓存 d={wp.Diameter} L={wp.Length}{(wp.ForceBalancing ? " 强制动平衡" : "")}");
        };

        TaskRows.Add(row);
        Console.WriteLine($"[HomeViewModel] 新增任务：版号={row.PlateNo} 序号={row.Sequence} 版长={row.Length}mm");
    }

    private void DeleteTaskRow(TaskRowViewModel row)
    {
        if (!row.CanDelete)
        {
            System.Windows.MessageBox.Show(
                "任务已经启动或已经分配线路，不能直接从列表删除。请保留该行用于现场状态追踪。",
                "不能删除任务",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        if (TaskRows.Remove(row))
            Console.WriteLine($"[HomeViewModel] 删除未启动任务：版号={row.PlateNo} 序号={row.Sequence}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  临时 MC 协议探测：页面加载时连一次，读状态写入卡片，排查通讯
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 开始页面只连接一次
    /// 一次性连接 MC 设备（总上料架 192.168.2.63:9000 + 货叉 ST711 IP:9000），
    /// 读 M800/M900 状态写入 StationCards，然后断开。用于排查 MC 协议是否通。
    /// </summary>
    private async Task ProbeMcDevicesAsync()
    {
        Console.WriteLine("[ProbeMC] ═══════ 开始 MC 设备探测（页面加载时连一次）═══════");
        var cards = StationCards;

        // ── 1. 探测总上料架+中转架 192.168.2.63:9000 ──────────────────
        {
            var rackClient = new MitsubishiMcClient(
                "192.168.2.63", 9000, MitsubishiMcClient.DeviceM,
                frameType: MitsubishiMcClient.McFrameType.A1E);
            try
            {
                Console.WriteLine("[ProbeMC] [总上料架] STEP1 尝试TCP连接 192.168.2.63:9000 (timeout=5s)...");
                using var cts = new CancellationTokenSource(5000);
                try { await rackClient.ConnectAsync(cts.Token); }
                catch (OperationCanceledException) { Console.WriteLine("[ProbeMC] [总上料架] ✘ TCP连接超时(5s) — PLC离线或IP/端口不对"); throw; }
                Console.WriteLine($"[ProbeMC] [总上料架] STEP2 TCP连接成功 ✓ IsConnected={rackClient.IsConnected}");

                // 读 M800 起始 2 字 (M800~M831)
                Console.WriteLine("[ProbeMC] [总上料架] STEP3 发送MC读帧 M800 2字...");
                ReadResult result;
                try { result = await rackClient.ReadAsync(800, 2, cts.Token); }
                catch (OperationCanceledException) { Console.WriteLine("[ProbeMC] [总上料架] ✘ MC读超时(5s) — TCP通但PLC无响应(A1E帧被拒?)"); throw; }
                ushort raw1 = (ushort)(result.IntValues.Length > 0 ? result.IntValues[0] : 0);
                ushort raw2 = (ushort)(result.IntValues.Length > 1 ? result.IntValues[1] : 0);
                Console.WriteLine($"[ProbeMC] [总上料架] ✔ 读成功 M800~M815=0x{raw1:X4} M816~M831=0x{raw2:X4}");

                // 读 D100 板长
                int plateLen = 0;
                try
                {
                    var dResult = await rackClient.ReadAsync(MitsubishiMcClient.DeviceD, 100, 1, cts.Token);
                    plateLen = dResult.IntValues.Length > 0 ? dResult.IntValues[0] : 0;
                    Console.WriteLine($"[ProbeMC] [总上料架] ✔ D100 板长={plateLen}mm");
                }
                catch (Exception ex) { Console.WriteLine($"[ProbeMC] [总上料架] ⚠ D100 读取失败: {ex.Message}"); }

                // ── 更新卡片 ──
                bool m800 = (raw1 & (1 << 0)) != 0;   // M800: 请求取料
                bool m811 = (raw1 & (1 << 11)) != 0;  // M811: 中转1有版
                bool m812 = (raw1 & (1 << 12)) != 0;  // M812: 中转2有版
                bool m813 = (raw1 & (1 << 13)) != 0;  // M813: 中转3有版

                if (cards.TryGetValue("ST001", out var c1))
                { c1.ConnectedBrush = Brushes.LimeGreen; c1.Status1 = m800 ? "请求取料" : "空闲"; c1.Status1Brush = m800 ? Brushes.Orange : Brushes.Green; c1.Status2 = $"板长={plateLen}mm"; c1.IpText = "192.168.2.63:9000"; }
                SetPlateCard(cards, "ST105", true, m811, "M811", "192.168.2.63:9000");
                SetPlateCard(cards, "ST101", true, m812, "M812", "192.168.2.63:9000");
                SetPlateCard(cards, "ST106", true, m813, "M813", "192.168.2.63:9000");
                Console.WriteLine("[ProbeMC] [总上料架] 卡片已更新: ST001✓ ST105✓ ST101✓ ST106✓");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProbeMC] [总上料架] ✘ 失败: {ex.GetType().Name} — {ex.Message}");
                // 标记断开
                if (cards.TryGetValue("ST001", out var c1)) { c1.ConnectedBrush = Brushes.Red; c1.Status1 = "无法连接"; c1.Status2 = ex.Message.Length > 30 ? ex.Message[..30] : ex.Message; }
            }
            finally
            {
                try { await rackClient.DisconnectAsync(); Console.WriteLine("[ProbeMC] [总上料架] 已断开 ✓"); }
                catch (Exception ex) { Console.WriteLine($"[ProbeMC] [总上料架] 断开异常: {ex.Message}"); }
            }
        }

        // ── 2. 探测货叉 (从 IpMap 取 ST711 的 IP) ─────────────────────
        {
            string? forkIp = null;
            if (IpMap.TryGetValue("ST711", out var ip) && !string.IsNullOrWhiteSpace(ip) && ip != "未配置IP")
                forkIp = ip;

            if (string.IsNullOrEmpty(forkIp))
            {
                Console.WriteLine("[ProbeMC] [货叉] ⚠ IpMap 中未找到 ST711 的IP，跳过");
                if (cards.TryGetValue("ST011", out var c)) { c.Status1 = "等待IP"; c.Status2 = "ST711未配置IP"; }
            }
            else
            {
                var forkClient = new MitsubishiMcClient(
                    forkIp, 9000, MitsubishiMcClient.DeviceM,
                    frameType: MitsubishiMcClient.McFrameType.A1E) { UseBitReadForM = true };
                try
                {
                    Console.WriteLine($"[ProbeMC] [货叉] STEP1 尝试TCP连接 {forkIp}:9000 (timeout=5s)...");
                    using var cts = new CancellationTokenSource(5000);
                    try { await forkClient.ConnectAsync(cts.Token); }
                    catch (OperationCanceledException) { Console.WriteLine("[ProbeMC] [货叉] ✘ TCP连接超时(5s) — PLC离线或IP/端口不对"); throw; }
                    Console.WriteLine($"[ProbeMC] [货叉] STEP2 TCP连接成功 ✓ IsConnected={forkClient.IsConnected}");

                    // 读 M900 (nibble编码, UseBitReadForM=true)
                    ReadResult result;
                    try { result = await forkClient.ReadAsync(900, 1, cts.Token); }
                    catch (OperationCanceledException) { Console.WriteLine("[ProbeMC] [货叉] ✘ MC读超时(5s)"); throw; }
                    ushort raw = (ushort)(result.IntValues.Length > 0 ? result.IntValues[0] : 0);
                    Console.WriteLine($"[ProbeMC] [货叉] ✔ M900~M915=0x{raw:X4}");

                    // ── 更新卡片 ──
                    bool hasPlate = (raw & (1 << 0)) != 0;
                    bool atPos1  = (raw & (1 << 2)) != 0;
                    bool atPos2  = (raw & (1 << 3)) != 0;
                    bool atPos3  = (raw & (1 << 4)) != 0;
                    bool atStdby = (raw & (1 << 1)) != 0;
                    string pos = atStdby ? "待机位" : atPos1 ? "1号位" : atPos2 ? "2号位" : atPos3 ? "3号位" : "待机位";

                    if (cards.TryGetValue("ST011", out var c2))
                    { c2.ConnectedBrush = Brushes.LimeGreen; c2.Status1 = hasPlate ? "有版" : "无版"; c2.Status1Brush = hasPlate ? Brushes.Orange : Brushes.Green; c2.Status2 = pos; c2.IpText = $"{forkIp}:9000"; }
                    Console.WriteLine($"[ProbeMC] [货叉] 卡片已更新: ST011✓ 有版={hasPlate} 位置={pos}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ProbeMC] [货叉] ✘ 失败: {ex.GetType().Name} — {ex.Message}");
                    if (cards.TryGetValue("ST011", out var c)) { c.ConnectedBrush = Brushes.Red; c.Status1 = "无法连接"; c.Status2 = ex.Message.Length > 30 ? ex.Message[..30] : ex.Message; }
                }
                finally
                {
                    try { await forkClient.DisconnectAsync(); Console.WriteLine("[ProbeMC] [货叉] 已断开 ✓"); }
                    catch (Exception ex) { Console.WriteLine($"[ProbeMC] [货叉] 断开异常: {ex.Message}"); }
                }
            }
        }

        // ── 3. 探测 192.168.2.64:9000 (研磨下料架 ST710) ─────
        {
            var client = new MitsubishiMcClient("192.168.2.64", 9000, MitsubishiMcClient.DeviceM,
                frameType: MitsubishiMcClient.McFrameType.A1E);
            try
            {
                Console.WriteLine("[ProbeMC] [2.64下料架] 尝试 TCP 连接...");
                using var cts = new CancellationTokenSource(5000);
                await client.ConnectAsync(cts.Token);
                Console.WriteLine("[ProbeMC] [2.64下料架] TCP连接成功 ✓");

                // 与GrindingFlowEngine保持一致: MC64下料架按对齐字读 M720起1字, M720=1允许放版, M730=1安全位置。
                var result = await client.ReadMAlignedWordAsync(720, 1, cts.Token);
                ushort raw = (ushort)(result.IntValues.Length > 0 ? result.IntValues[0] : 0);
                Console.WriteLine($"[ProbeMC] [2.64下料架] ✔ 读成功 M720~M735=0x{raw:X4}");

                bool m720CanPlace = (raw & 1) != 0;        // M720=1: 下料架允许放版
                bool m730Safe = (raw & (1 << 10)) != 0;   // M730=1: 下料架在安全位置
                bool unloadReady = m720CanPlace && m730Safe;
                // 读 D200: 研磨下料架最近写入/显示的工件直径。
                int unloadDiameter = 0;
                try { var d = await client.ReadAsync(MitsubishiMcClient.DeviceD, 200, 1, cts.Token); unloadDiameter = d.IntValues.Length > 0 ? d.IntValues[0] : 0; }
                catch { }

                string unloadState = unloadReady ? "可放料" : (!m720CanPlace ? "不可放料" : "未到安全位");
                var unloadBrush = unloadReady ? Brushes.Green : Brushes.Orange;
                if (cards.TryGetValue("ST013", out var c13)) { c13.ConnectedBrush = Brushes.LimeGreen; c13.Status1 = unloadState; c13.Status1Brush = unloadBrush; c13.Status2 = $"D200直径={unloadDiameter}"; c13.IpText = "192.168.2.64:9000"; }
                if (cards.TryGetValue("ST710", out var c710)) { c710.ConnectedBrush = Brushes.LimeGreen; c710.Status1 = unloadState; c710.Status1Brush = unloadBrush; c710.Status2 = $"M720={(m720CanPlace ? 1 : 0)} M730={(m730Safe ? 1 : 0)}"; c710.IpText = "192.168.2.64:9000"; }
                Console.WriteLine("[ProbeMC] [2.64下料架] 卡片已更新: ST013✓ ST710✓");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProbeMC] [2.64下料架] ✘ 失败: {ex.GetType().Name} — {ex.Message}");
                if (cards.TryGetValue("ST013", out var c)) { c.ConnectedBrush = Brushes.Red; c.Status1 = "无法连接"; }
                if (cards.TryGetValue("ST710", out var c2)) { c2.ConnectedBrush = Brushes.Red; c2.Status1 = "无法连接"; }
            }
            finally { try { await client.DisconnectAsync(); } catch { } }
        }

        // ── 4. 探测 192.168.2.65:9000 (研磨机上料架 ST709 + 动平衡) ────
        {
            var client = new MitsubishiMcClient("192.168.2.65", 9000, MitsubishiMcClient.DeviceM,
                frameType: MitsubishiMcClient.McFrameType.A1E);
            try
            {
                Console.WriteLine("[ProbeMC] [2.65上料架] 尝试 TCP 连接...");
                using var cts = new CancellationTokenSource(5000);
                await client.ConnectAsync(cts.Token);
                Console.WriteLine("[ProbeMC] [2.65上料架] TCP连接成功 ✓");

                // 与Balancing/Grinding引擎保持一致: FX按字读M区必须16点对齐。
                var r688 = await client.ReadMAlignedWordAsync(688, 1, cts.Token); // M700 = M688 bit12
                var r704 = await client.ReadMAlignedWordAsync(704, 1, cts.Token); // M710 = M704 bit6
                var r720 = await client.ReadMAlignedWordAsync(720, 1, cts.Token); // M720 bit0, M730 bit10
                ushort raw688 = (ushort)(r688.IntValues.Length > 0 ? r688.IntValues[0] : 0);
                ushort raw704 = (ushort)(r704.IntValues.Length > 0 ? r704.IntValues[0] : 0);
                ushort raw720 = (ushort)(r720.IntValues.Length > 0 ? r720.IntValues[0] : 0);
                Console.WriteLine($"[ProbeMC] [2.65上料架] ✔ 读成功 M688=0x{raw688:X4} M704=0x{raw704:X4} M720=0x{raw720:X4}");

                bool m700 = (raw688 & (1 << 12)) != 0;  // M700: 人工动平衡后料架2有板, 允许取板
                bool m710 = (raw704 & (1 << 6)) != 0;   // M710: 动平衡料架1允许放版
                bool m720 = (raw720 & 1) != 0;           // M720: 研磨上料架1号位允许放版
                bool m730 = (raw720 & (1 << 10)) != 0;   // M730: 研磨上料架3号位末端有板, 允许研磨天车取板
                // D100=动平衡后料架2测量直径; D200=研磨上料架2号位测长。
                int d100Diameter = 0;
                int d200Length = 0;
                try { var d = await client.ReadAsync(MitsubishiMcClient.DeviceD, 100, 1, cts.Token); d100Diameter = d.IntValues.Length > 0 ? d.IntValues[0] : 0; }
                catch { }
                try { var d = await client.ReadAsync(MitsubishiMcClient.DeviceD, 200, 1, cts.Token); d200Length = d.IntValues.Length > 0 ? d.IntValues[0] : 0; }
                catch { }

                if (cards.TryGetValue("ST709", out var c709)) { c709.ConnectedBrush = Brushes.LimeGreen; c709.Status1 = m730 ? "可取板" : "末位无版"; c709.Status1Brush = m730 ? Brushes.Orange : Brushes.Green; c709.Status2 = $"M730 D200长度={d200Length}"; c709.IpText = "192.168.2.65:9000"; }
                if (cards.TryGetValue("ST009", out var c009)) { c009.ConnectedBrush = Brushes.LimeGreen; c009.Status1 = m700 ? "可取板" : "无板"; c009.Status1Brush = m700 ? Brushes.Orange : Brushes.Green; c009.Status2 = $"M700 D100直径={d100Diameter}"; c009.IpText = "192.168.2.65:9000"; }
                if (cards.TryGetValue("ST008", out var c008)) { c008.ConnectedBrush = Brushes.LimeGreen; c008.Status1 = m710 ? "可放料" : "不可放料"; c008.Status1Brush = m710 ? Brushes.Green : Brushes.Orange; c008.Status2 = "M710"; c008.IpText = "192.168.2.65:9000"; }
                if (cards.TryGetValue("ST010", out var c010)) { c010.ConnectedBrush = Brushes.LimeGreen; c010.Status1 = m720 ? "可放料" : "不可放料"; c010.Status1Brush = m720 ? Brushes.Green : Brushes.Orange; c010.Status2 = "M720"; c010.IpText = "192.168.2.65:9000"; }
                Console.WriteLine("[ProbeMC] [2.65上料架] 卡片已更新: ST709✓ ST009✓ ST008✓ ST010✓");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProbeMC] [2.65上料架] ✘ 失败: {ex.GetType().Name} — {ex.Message}");
                if (cards.TryGetValue("ST709", out var c)) { c.ConnectedBrush = Brushes.Red; c.Status1 = "无法连接"; }
            }
            finally { try { await client.DisconnectAsync(); } catch { } }
        }

        Console.WriteLine("[ProbeMC] ═══════ MC 设备探测结束（4路: 2.63✓ 货叉✓ 2.64✓ 2.65✓）═══════");
    }

    /// <summary>
    /// 一次性探测 Syntec 双头镗 (ST401 192.168.2.66)。
    /// 连→读 R6101 请求数据信号→写 ST401 卡片→断开。
    /// </summary>
    private async Task ProbeSyntecDevicesAsync()
    {
        Console.WriteLine("[ProbeSyntec] ═══════ Syntec 双头镗探测 ═══════");
        var cards = StationCards;

        // ST401(XAML显示码)→DB站号ST103 192.168.2.66
        string? ip = null;
        if (IpMap.TryGetValue("ST103", out var st103Ip) && !string.IsNullOrWhiteSpace(st103Ip) && st103Ip != "未配置IP")
            ip = st103Ip;

        if (string.IsNullOrEmpty(ip))
        {
            Console.WriteLine("[ProbeSyntec] [双头镗] ⚠ IpMap 中未找到 ST103 IP，跳过");
            if (cards.TryGetValue("ST401", out var c)) { c.Status1 = "等待IP"; c.Status2 = "ST103未配置IP"; }
            return;
        }

        try
        {
            Console.WriteLine($"[ProbeSyntec] [双头镗] 尝试连接 {ip}:502...");
            using var svc = new SyntecBoringService(ip);
            using var cts = new CancellationTokenSource(5000);
            await svc.ConnectAsync(cts.Token);
            Console.WriteLine("[ProbeSyntec] [双头镗] 连接成功 ✓");

            // 读 R6101 请求数据信号
            bool reqData = await svc.IsRequestDataAsync(cts.Token);
            Console.WriteLine($"[ProbeSyntec] [双头镗] ✔ R6101 请求数据={reqData}");

            if (cards.TryGetValue("ST401", out var c))
            {
                c.ConnectedBrush = Brushes.LimeGreen;
                c.Status1 = reqData ? "请求数据" : "空闲";
                c.Status1Brush = reqData ? Brushes.Orange : Brushes.Green;
                c.Status2 = $"R6101={(reqData?1:0)} {ip}:502";
                c.IpText = $"{ip}:502";
            }
            Console.WriteLine("[ProbeSyntec] [双头镗] 卡片已更新: ST401✓");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProbeSyntec] [双头镗] ✘ 失败: {ex.GetType().Name} — {ex.Message}");
            if (cards.TryGetValue("ST401", out var c)) { c.ConnectedBrush = Brushes.Red; c.Status1 = "无法连接"; c.Status2 = "Syntec SDK?"; }
        }
        Console.WriteLine("[ProbeSyntec] ═══════ 结束 ═══════");
    }
}

