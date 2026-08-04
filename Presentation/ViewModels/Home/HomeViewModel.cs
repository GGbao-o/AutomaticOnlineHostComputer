
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
using AutomaticOnlineHostComputer.Service.OperationalEvents;
using RackAddr = AutomaticOnlineHostComputer.Communication.DeviceAddresses.CenteringRackAddress;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

public sealed class HomeViewModel : ObservableObject
{
    private readonly ManagementQueryService _queryService;
    private readonly PositionUpdateService _positionService;
    private readonly AttentionEventCenter _attentionEvents;
    private readonly IOperationalEventReporter _exceptionReporter;
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

    /// <summary>创建配置页面 ViewModel。提供天车连接供"读当前位置"功能使用。</summary>
    public Config.ConfigPageViewModel CreateConfigPageViewModel()
        => new(_cfg, craneNo =>
        {
            CraneService? service;
            try { service = _craneCache.GetOrCreateService(craneNo); }
            catch { service = null; }
            return Task.FromResult(service);
        });

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
            if (!await EnsureCranePositionsReadyForStartAsync(
                    new[] { (_cfg.Grinding.CraneNo, "研磨天车") }, "研磨引擎启动/恢复前"))
                return;

            lock (_lineSafetyPopupLock) _grindingSafetyPopupShown = false;
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
            InnerTaper = 10, CornerSize = 8,
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
            InnerTaper = 10, CornerSize = 8,
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
    private string _lastErpInvalidBackupSignature = string.Empty;
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

    public HomeViewModel(ManagementQueryService queryService, PositionUpdateService positionService,
        AttentionEventCenter attentionEvents, IOperationalEventReporter exceptionReporter)
    {
        Console.WriteLine("HomeViewModel页面启动");
        _queryService = queryService;
        _positionService = positionService;
        _attentionEvents = attentionEvents;
        _exceptionReporter = exceptionReporter;
        _cfg = MotionConfig.Load();  // 提前加载，引擎创建和UI同步都要用
        _craneCache = new CraneConnectionCache(_cfg);
        _manipulatorCache = new ManipulatorConnectionCache(_cfg);

        // 引擎依赖天车/机械手缓存，由 HomeVM 创建并持有引用
        _flowEngine = new ProductionFlowEngine(_craneCache, _manipulatorCache, _positionService, _cfg, _exceptionReporter);

        Crane1F = new CraneCardViewModel(1, _craneCache);
        Crane1R = new CraneCardViewModel(2, _craneCache);
        Crane2F = new CraneCardViewModel(3, _craneCache);
        Crane2R = new CraneCardViewModel(4, _craneCache);
        CraneGL = new CraneCardViewModel(5, _craneCache);

        ManualControl = new CraneManualControlViewModel(_craneCache, _manipulatorCache);

        Manipulator1 = new ManipulatorCardViewModel(1, _manipulatorCache);
        Manipulator2 = new ManipulatorCardViewModel(2, _manipulatorCache);
        Manipulator3 = new ManipulatorCardViewModel(3, _manipulatorCache);

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
        AutoStartTasksCommand = new AsyncRelayCommand(AutoStartTasksAsync, nameof(AutoStartTasksCommand));

        // 测试动平衡默认值
        BalancingTestDiameter = 147;
        BalancingTestLength = 700;
        SelectedBalancingPosition = "M817";

        Console.WriteLine("[HomeViewModel] 初始化完成：天车+机械手+研磨机+手动控制+流程引擎 VM已创建。");
    }

    public Dictionary<string, string> IpMap { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 全厂状态总览卡片字典。key = 站号（如 ST103），value = 该站位的卡片 ViewModel。
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
    private CancellationTokenSource? _grinderPollCts;
    private readonly List<Task> _grinderPollTasks = new();

    /// <summary>是否正在运行研磨自动流程</summary>
    private bool _isGrindingRunning;
    private bool _grindingSafetyPopupShown;
    public bool IsGrindingRunning { get => _isGrindingRunning; set { if (SetField(ref _isGrindingRunning, value)) OnPropertyChanged(nameof(GrindingToggleText)); } }

    /// <summary>启动/暂停按钮文本</summary>
    public string GrindingToggleText => _isGrindingRunning ? "暂停研磨" : "启动研磨";

    /// <summary>已缓存研磨工件数量</summary>
    public int GrindingCachedCount => _grindingEngine?.CachedCount ?? 0;
    public string GrindingCacheDetail => FormatCacheDetail(_grindingEngine?.GetCachedWorkpiecesSnapshot());

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
    private CenteringRackService? _frontRackDispatchSvc;
    private CancellationTokenSource? _frontDispatchCts;
    private Task? _frontDispatchTask;
    private readonly object _frontDispatchLock = new();
    private readonly Queue<FrontDispatchItem> _frontDispatchQueue = new();
    private bool _frontDispatchWaitingM800Clear;
    private int _frontDispatchLoopCycle;
    /// <summary>总上料架全局分线的统一轮询周期。总上料架是一板一板到位，1秒足够且避免无意义刷屏。</summary>
    private static readonly TimeSpan FrontDispatchPollInterval = TimeSpan.FromSeconds(1);
    /// <summary>两线压力相同时的交替记忆；只在成功写入线路前端缓存后更新。</summary>
    private int _lastFrontDispatchLine;

    private sealed class FrontDispatchItem
    {
        public FrontDispatchItem(TaskRowViewModel row, WorkpieceCache workpiece)
        {
            Row = row;
            Workpiece = workpiece;
        }

        public TaskRowViewModel Row { get; }
        public WorkpieceCache Workpiece { get; }
    }

    /// <summary>后台任务：从引擎 DeviceStatus 同步到 UI 卡片（不另建连接）</summary>
    private async Task SyncLine1StatusToCardsAsync(CancellationToken ct)
    {
        // XAML 显示码 → 数据来源:
        //   引擎托管(仅运行时刷): ST001→ST007, ST002→ST002, ST011→ST711, ST103→1号双头镗, ST501→ST501, ST901→ST901, ST105/ST101/ST106→M811~M813
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
                // ST001/ST002是两条线共享的同一批物理设备，由一个确定性入口合并显示，
                // 避免双线各自刷新时同一张卡片在两套文本之间来回覆盖。
                SyncSharedFrontCards(cards);

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
                    EnsureCard("ST103", "一号双头镗");
                    EnsureCard("ST501", "一号打号机");
                    EnsureCard("ST105", "1号线中转架1");
                    EnsureCard("ST101", "1号线中转架2");
                    EnsureCard("ST106", "1号线中转架3");
                    EnsureCard("ST108", "一号斜床");
                    EnsureCard("ST109", "二号斜床");
                    EnsureCard("ST111", "三号斜床");
                    EnsureCard("ST110", "四号斜床");
                    EnsureCard("ST112", "五号斜床");

                    // ── 货叉1 ST011 (MC:192.168.2.88:9000 M900~M914) ──
                    if (cards.TryGetValue("ST011", out var c3))
                    { c3.ConnectedBrush = ds.ForkConnected ? Brushes.LimeGreen : Brushes.Gray;
                      c3.Status1 = ds.ForkConnected ? (ds.ForkHasPlate ? "有版" : "无版") : "断开";
                      c3.Status1Brush = ds.ForkConnected ? (ds.ForkHasPlate ? Brushes.Orange : Brushes.Green) : Brushes.Gray;
                      c3.Status2 = ds.ForkConnected ? $"{ds.ForkPosition} {ds.ForkCommand}" : "—"; }

                    // ── 双头镗 ST103 (Modbus R区: R*2+1) ──
                    SetBoringCard(cards, "ST103", ds.BoringConnected, ds.BoringSnapshotValid,
                        ds.Boring_R6101, ds.Boring_R6102, ds.Boring_R6103,
                        ds.Boring_R6104, ds.Boring_R6107, ds.Boring_R6108);

                    // ── 打号机 ST501 (文件握手:D:\1\ A/B/3) ──
                    if (cards.TryGetValue("ST501", out var c5))
                    { c5.ConnectedBrush = ds.MarkerConnected ? Brushes.LimeGreen : Brushes.IndianRed;
                      c5.Status1 = ds.MarkerConnected ? "就绪" : "共享不可访问";
                      c5.Status1Brush = ds.MarkerConnected ? Brushes.Green : Brushes.IndianRed;
                      c5.Status2 = ds.MarkerStatusText; }

                    // ── 1号线天车前 ST901 (Modbus:192.168.2.81:502) ──
                    if (cards.TryGetValue("ST901", out var c6))
                    { c6.ConnectedBrush = ds.CraneFrontConnected ? Brushes.LimeGreen : Brushes.Gray;
                      c6.Status1 = ds.CraneFrontConnected ? "就绪" : "断开";
                      c6.Status1Brush = ds.CraneFrontConnected ? Brushes.Green : Brushes.Gray;
                      c6.Status2 = ds.CraneFrontConnected ? "192.168.2.81:502" : "—"; }

                    SetPlateCard(cards, "ST105", ds.RackConnected, !ds.TransferRack1Free, "M811", "192.168.2.63:9000");
                    SetPlateCard(cards, "ST101", ds.RackConnected, !ds.TransferRack2Free, "M812", "192.168.2.63:9000");
                    SetPlateCard(cards, "ST106", ds.RackConnected, !ds.TransferRack3Free, "M813", "192.168.2.63:9000");
                }
                else
                {
                    MarkCardsNotStarted(cards, "ST011", "ST103", "ST501", "ST901", "ST105", "ST101", "ST106");
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

                    // 斜床 ST108~ST112 — 成功显示真实信号, 失败也必须覆盖旧状态。
                    SetSkewCard(cards, "ST108", rds.Skew1Connected, rds.Skew1State, rds.Skew1Signals);
                    SetSkewCard(cards, "ST109", rds.Skew2Connected, rds.Skew2State, rds.Skew2Signals);
                    SetSkewCard(cards, "ST111", rds.Skew3Connected, rds.Skew3State, rds.Skew3Signals);
                    SetSkewCard(cards, "ST110", rds.Skew4Connected, rds.Skew4State, rds.Skew4Signals);
                    SetSkewCard(cards, "ST112", rds.Skew5Connected, rds.Skew5State, rds.Skew5Signals);

                }
                else
                {
                    MarkCardsNotStarted(cards, "ST902", "ST108", "ST109", "ST111", "ST110", "ST112");
                }

                SyncBalancingCards(cards);
                // 三个计数属性由引擎实时计算，定时通知可覆盖自动出队等非UI命令触发的变化。
                OnPropertyChanged(nameof(Line1CachedCount));
                OnPropertyChanged(nameof(Line2CachedCount));
                OnPropertyChanged(nameof(GrindingCachedCount));
                OnPropertyChanged(nameof(Line1CacheDetail));
                OnPropertyChanged(nameof(Line2CacheDetail));
                OnPropertyChanged(nameof(GrindingCacheDetail));
                RefreshFlowStatusHoverDetails();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HomeViewModel] 1号线状态卡同步异常: {ex.GetType().Name} - {ex.Message}");
            }
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
                    EnsureCard("ST001", "总上料架");
                    EnsureCard("ST002", "机械手1");
                    EnsureCard("ST712", "货叉2");
                    EnsureCard("ST402", "二号双头镗");
                    EnsureCard("ST502", "二号打号机");
                    EnsureCard("ST104", "2号线天车前");
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
                      c3.Status1Brush = ds.ForkConnected ? (ds.ForkHasPlate ? Brushes.Orange : Brushes.Green) : Brushes.Gray;
                      c3.Status2 = ds.ForkConnected ? $"{ds.ForkPosition} {ds.ForkCommand}" : "—"; }

                    // 双头镗 ST402
                    SetBoringCard(cards, "ST402", ds.BoringConnected, ds.BoringSnapshotValid,
                        ds.Boring_R6101, ds.Boring_R6102, ds.Boring_R6103,
                        ds.Boring_R6104, ds.Boring_R6107, ds.Boring_R6108);

                    // 打号机 ST502
                    if (cards.TryGetValue("ST502", out var c5))
                    { c5.ConnectedBrush = ds.MarkerConnected ? Brushes.LimeGreen : Brushes.IndianRed;
                      c5.Status1 = ds.MarkerConnected ? "就绪" : "共享不可访问";
                      c5.Status1Brush = ds.MarkerConnected ? Brushes.Green : Brushes.IndianRed;
                      c5.Status2 = ds.MarkerStatusText; }

                    // 天车前 ST104
                    if (cards.TryGetValue("ST104", out var c6))
                    { c6.ConnectedBrush = ds.CraneFrontConnected ? Brushes.LimeGreen : Brushes.Gray;
                      c6.Status1 = ds.CraneFrontConnected ? "就绪" : "断开";
                      c6.Status1Brush = ds.CraneFrontConnected ? Brushes.Green : Brushes.Gray;
                      c6.Status2 = ds.CraneFrontConnected ? "192.168.2.83:502" : "—"; }

                    // 中转架
                    SetPlateCard(cards, "ST016", ds.RackConnected, !ds.TransferRack4Free, "M814", "192.168.2.63:9000");
                    SetPlateCard(cards, "ST017", ds.RackConnected, !ds.TransferRack5Free, "M815", "192.168.2.63:9000");
                    SetPlateCard(cards, "ST018", ds.RackConnected, !ds.TransferRack6Free, "M816", "192.168.2.63:9000");
                }
                else
                {
                    MarkCardsNotStarted(cards, "ST712", "ST402", "ST502", "ST104", "ST016", "ST017", "ST018");
                }

                // 后端引擎状态
                if (engineRunning && _line2RearEngine?.DeviceStatus is { } rds)
                {
                    EnsureCard("ST904", "2号线天车后");
                    if (cards.TryGetValue("ST904", out var cr))
                    { cr.ConnectedBrush = rds.CraneRearConnected ? Brushes.LimeGreen : Brushes.Gray;
                      cr.Status1 = rds.CraneRearConnected ? "就绪" : "断开";
                      cr.Status1Brush = rds.CraneRearConnected ? Brushes.Green : Brushes.Gray;
                      cr.Status2 = rds.CraneRearConnected ? "192.168.2.84:502" : "—"; }

                    SetSkewCard(cards, "ST606", rds.Skew1Connected, rds.Skew1State, rds.Skew1Signals);
                    SetSkewCard(cards, "ST607", rds.Skew2Connected, rds.Skew2State, rds.Skew2Signals);
                    SetSkewCard(cards, "ST608", rds.Skew3Connected, rds.Skew3State, rds.Skew3Signals);
                    SetSkewCard(cards, "ST609", rds.Skew4Connected, rds.Skew4State, rds.Skew4Signals);
                    SetSkewCard(cards, "ST610", rds.Skew5Connected, rds.Skew5State, rds.Skew5Signals);
                }
                else
                {
                    MarkCardsNotStarted(cards, "ST904", "ST606", "ST607", "ST608", "ST609", "ST610");
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HomeViewModel] 2号线状态卡同步异常: {ex.GetType().Name} - {ex.Message}");
            }
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

    private static void MarkCardsNotStarted(Dictionary<string, StationCardViewModel> cards, params string[] codes)
    {
        foreach (var code in codes)
        {
            if (!cards.TryGetValue(code, out var card)) continue;
            card.ConnectedBrush = Brushes.Gray;
            card.Status1 = "未启动";
            card.Status1Brush = Brushes.Gray;
            card.Status2 = "线路未启动";
        }
    }

    /// <summary>将第三部分机械手卡片的状态镜像到第四部分 StatusCard（不建新连接，复用已有 VM）。</summary>
    private static string To01(bool b) => b ? "1" : "0";

    private static string BuildGrinderOverviewState(bool fault, bool statusAlarm, bool stoneAlarm, int stoneNo,
        bool reqData, bool reqLoad, bool clamped, bool reqUnload, bool unclamp, bool busy, int? machineStatus = null)
    {
        if (stoneAlarm) return $"⚠ 磨石{stoneNo}厚度报警 · 已停止自动分配";
        if (fault || statusAlarm) return statusAlarm ? "报警" : "故障";
        if (reqUnload) return "请求下料";
        if (reqLoad) return "请求上料";
        if (clamped) return "锁紧完成";
        if (reqData) return "请求数据";
        if (unclamp) return "松开完成";
        if (busy) return "加工中";
        return machineStatus is > 0 ? $"忙碌中({machineStatus})" : "空闲";
    }

    private static string BuildBoringOverviewState(bool reqData, bool dataDone, bool reqLoad, bool loadDone, bool reqUnload, bool unloadDone)
    {
        if (reqUnload) return "请求下料";
        if (unloadDone) return "下料完成";
        if (reqLoad) return "请求上料";
        if (loadDone) return "加工中";
        if (dataDone) return "数据已下发";
        if (reqData) return "请求数据";
        return "加工中/等待";
    }

    private static string BuildBoringSignalText(bool reqData, bool dataDone, bool reqLoad, bool loadDone, bool reqUnload, bool unloadDone)
        => $"R6101={To01(reqData)} R6102={To01(dataDone)} R6103={To01(reqLoad)} R6104={To01(loadDone)} R6107={To01(reqUnload)} R6108={To01(unloadDone)}";

    private static void SetBoringCard(Dictionary<string, StationCardViewModel> cards, string code,
        bool connected, bool snapshotValid, bool reqData, bool dataDone, bool reqLoad,
        bool loadDone, bool reqUnload, bool unloadDone)
    {
        if (!cards.TryGetValue(code, out var card)) return;

        card.ConnectedBrush = connected ? Brushes.LimeGreen : Brushes.Gray;
        if (!connected)
        {
            card.Status1 = "断开";
            card.Status1Brush = Brushes.Gray;
            card.Status2 = "—";
            return;
        }

        if (!snapshotValid)
        {
            card.Status1 = "读取失败";
            card.Status1Brush = Brushes.Red;
            card.Status2 = "R6101/R6102/R6103/R6104/R6107/R6108快照失败";
            return;
        }

        card.Status1 = BuildBoringOverviewState(reqData, dataDone, reqLoad, loadDone, reqUnload, unloadDone);
        card.Status1Brush = GetSignalStateBrush(card.Status1);
        card.Status2 = BuildBoringSignalText(reqData, dataDone, reqLoad, loadDone, reqUnload, unloadDone);
    }

    private static Brush GetSignalStateBrush(string state)
    {
        return state switch
        {
            "故障" or "报警" or "读失败" or "读取失败" or "连接失败" or "无法连接" => Brushes.Red,
            "断开" or "连接异常" or "连接暂缓" => Brushes.Gray,
            "空闲" or "就绪" or "安全位" => Brushes.Green,
            _ => Brushes.Orange
        };
    }

    private static void SyncManipulatorMirror(Dictionary<string, StationCardViewModel> cards,
        string code, ManipulatorCardViewModel manipVm, string name)
    {
        if (!cards.TryGetValue(code, out var sc)) return;
        sc.ConnectedBrush = manipVm.ConnectedBrush;
        sc.Status1 = manipVm.Line1;          // "已连接，就绪" / "未连接" / "故障"
        sc.Status1Brush = manipVm.Line1Brush;
        sc.Status2 = $"{manipVm.Line5}";     // Y/Z 坐标
    }

    /// <summary>
    /// ST001和ST002是两条前端共用的物理设备。统一合并两个只读快照，避免两个UI循环交替覆盖同一张卡片。
    /// </summary>
    private void SyncSharedFrontCards(Dictionary<string, StationCardViewModel> cards)
    {
        var line1Engine = _line1Engine;
        var line2Engine = _line2Engine;
        var line1 = line1Engine is { IsRunning: true } ? line1Engine.DeviceStatus : null;
        var line2 = line2Engine is { IsRunning: true } ? line2Engine.DeviceStatus : null;
        if (line1 == null && line2 == null)
        {
            MarkCardsNotStarted(cards, "ST001", "ST002");
            return;
        }

        // 两条前端都维护同一组共享设备。页面只选择最新且未过期的整轮快照，
        // 避免1号线暂停后的旧绿色状态覆盖2号线较新的实际结果。
        DateTime nowUtc = DateTime.UtcNow;
        bool line1Fresh = line1 != null
                          && line1Engine?.IsPaused != true
                          && DisplaySnapshotFreshness.IsFresh(line1.SnapshotAtUtc, nowUtc);
        bool line2Fresh = line2 != null
                          && line2Engine?.IsPaused != true
                          && DisplaySnapshotFreshness.IsFresh(line2.SnapshotAtUtc, nowUtc);

        if (!line1Fresh && !line2Fresh)
        {
            bool paused = line1Engine?.IsPaused == true || line2Engine?.IsPaused == true;
            string state = paused ? "暂停/最后已知" : "数据过期";
            DateTime latest = new[] { line1?.SnapshotAtUtc ?? default, line2?.SnapshotAtUtc ?? default }.Max();
            string detail = latest == default
                ? "等待前端完成首轮状态采集"
                : $"最近快照 {latest.ToLocalTime():HH:mm:ss}，不作为当前设备证据";
            foreach (string code in new[] { "ST001", "ST002" })
            {
                if (!cards.TryGetValue(code, out var card)) continue;
                card.ConnectedBrush = Brushes.Gray;
                card.Status1 = state;
                card.Status1Brush = Brushes.Orange;
                card.Status2 = detail;
            }
            return;
        }

        bool useLine1 = line1Fresh && (!line2Fresh || line1!.SnapshotAtUtc >= line2!.SnapshotAtUtc);

        if (cards.TryGetValue("ST001", out var rackCard))
        {
            bool line1RackOk = line1Fresh && line1?.RackConnected == true;
            bool line2RackOk = line2Fresh && line2?.RackConnected == true;
            bool connected = useLine1 ? line1!.RackConnected : line2!.RackConnected;
            bool requestPickup = connected && (useLine1 ? line1!.RackRequestPickup : line2!.RackRequestPickup);
            int plateLength = useLine1 ? line1!.RackPlateLength : line2!.RackPlateLength;

            rackCard.ConnectedBrush = connected ? Brushes.LimeGreen : Brushes.Gray;
            rackCard.Status1 = connected ? (requestPickup ? "请求取料" : "空闲") : "断开";
            rackCard.Status1Brush = connected
                ? (requestPickup ? Brushes.Orange : Brushes.Green)
                : Brushes.Gray;

            var rackStates = new List<string> { $"板长={plateLength}mm" };
            if (line1RackOk)
            {
                rackStates.Add($"中1={(line1!.TransferRack1Free ? "空" : "满")}");
                rackStates.Add($"2={(line1.TransferRack2Free ? "空" : "满")}");
                rackStates.Add($"3={(line1.TransferRack3Free ? "空" : "满")}");
            }
            if (line2RackOk)
            {
                rackStates.Add($"中4={(line2!.TransferRack4Free ? "空" : "满")}");
                rackStates.Add($"5={(line2.TransferRack5Free ? "空" : "满")}");
                rackStates.Add($"6={(line2.TransferRack6Free ? "空" : "满")}");
            }
            rackCard.Status2 = connected ? string.Join(" ", rackStates) : "—";
        }

        if (cards.TryGetValue("ST002", out var manipulatorCard))
        {
            bool connected = useLine1 ? line1!.Manipulator1Connected : line2!.Manipulator1Connected;
            bool safe = connected && (useLine1 ? line1!.Manipulator1Safe : line2!.Manipulator1Safe);
            int y = useLine1 ? line1!.Manipulator1Y : line2!.Manipulator1Y;

            manipulatorCard.ConnectedBrush = connected ? Brushes.LimeGreen : Brushes.Gray;
            manipulatorCard.Status1 = connected ? (safe ? "安全位" : $"Y={y}") : "断开";
            manipulatorCard.Status1Brush = connected ? (safe ? Brushes.Green : Brushes.Orange) : Brushes.Gray;
            // 机械手1是两线共用设备：任一现场确认安全点都表示已离开两条前天车危险区。
            manipulatorCard.Status2 = connected
                ? $"安全Y:1线={_cfg.SkewBed.Manipulator1Line1SafeY} 2线={_cfg.SkewBed.Manipulator1Line2SafeY}"
                : "—";
        }
    }

    private static void SetPlateCard(Dictionary<string, StationCardViewModel> cards,
        string code, bool connected, bool hasPlate, string status2, string ipText)
    {
        if (!cards.TryGetValue(code, out var card)) return;
        bool readFailed = !connected && status2.Contains("失败", StringComparison.Ordinal);
        card.ConnectedBrush = connected ? Brushes.LimeGreen : Brushes.Gray;
        card.Status1 = connected ? (hasPlate ? "有版" : "空闲") : (readFailed ? "读取失败" : "断开");
        card.Status1Brush = connected ? (hasPlate ? Brushes.Orange : Brushes.Green) : (readFailed ? Brushes.Red : Brushes.Gray);
        card.Status2 = connected || readFailed ? status2 : "—";
        card.IpText = connected ? ipText : "未连接";
    }

    private static void SetSkewCard(Dictionary<string, StationCardViewModel> cards,
        string code, bool connected, string state, string signals)
    {
        if (!cards.TryGetValue(code, out var card)) return;

        string displayState = string.IsNullOrWhiteSpace(state) ? (connected ? "未知" : "断开") : state;
        string displaySignals = string.IsNullOrWhiteSpace(signals) ? "—" : signals;

        card.ConnectedBrush = connected ? Brushes.LimeGreen : Brushes.Red;
        card.Status1 = connected ? displayState : (displayState == "--" ? "读失败" : displayState);
        card.Status1Brush = connected ? GetSignalStateBrush(displayState) : Brushes.Red;
        card.Status2 = connected ? displaySignals : ShortStatusText(displaySignals);
    }

    private string _line1TransferRackTooltip = string.Empty;
    public string Line1TransferRackTooltip { get => _line1TransferRackTooltip; private set => SetField(ref _line1TransferRackTooltip, value); }
    private string _line2TransferRackTooltip = string.Empty;
    public string Line2TransferRackTooltip { get => _line2TransferRackTooltip; private set => SetField(ref _line2TransferRackTooltip, value); }
    private string _line1SkewTooltip = string.Empty;
    public string Line1SkewTooltip { get => _line1SkewTooltip; private set => SetField(ref _line1SkewTooltip, value); }
    private string _line2SkewTooltip = string.Empty;
    public string Line2SkewTooltip { get => _line2SkewTooltip; private set => SetField(ref _line2SkewTooltip, value); }
    private string _grindingTooltip = string.Empty;
    public string GrindingTooltip { get => _grindingTooltip; private set => SetField(ref _grindingTooltip, value); }

    /// <summary>把引擎只读快照映射为全流程页Tooltip；不创建连接、不读写设备。</summary>
    private void RefreshFlowStatusHoverDetails()
    {
        var line1Skews = _line1RearEngine?.GetSkewStationSnapshots();
        var line2Skews = _line2RearEngine?.GetSkewStationSnapshots();
        ApplyProcessTooltips(line1Skews);
        ApplyProcessTooltips(line2Skews);
        var grinders = _grindingEngine?.GetGrinderStationSnapshots();
        ApplyProcessTooltips(grinders);

        Line1SkewTooltip = FormatProcessGroupTooltip(line1Skews, "1号线斜床");
        Line2SkewTooltip = FormatProcessGroupTooltip(line2Skews, "2号线斜床");
        GrindingTooltip = FormatProcessGroupTooltip(grinders, "研磨机");

        Line1TransferRackTooltip = FormatTransferRackTooltip(_line1RearEngine?.GetTransferRackSnapshots());
        Line2TransferRackTooltip = FormatTransferRackTooltip(_line2RearEngine?.GetTransferRackSnapshots());

        Crane1F.FlowTaskTooltipText = FormatCraneTooltip(_line1Engine?.GetCraneTaskSnapshot());
        Crane1R.FlowTaskTooltipText = FormatCraneTooltip(_line1RearEngine?.GetCraneTaskSnapshot());
        Crane2F.FlowTaskTooltipText = FormatCraneTooltip(_line2Engine?.GetCraneTaskSnapshot());
        Crane2R.FlowTaskTooltipText = FormatCraneTooltip(_line2RearEngine?.GetCraneTaskSnapshot());
        CraneGL.FlowTaskTooltipText = FormatCraneTooltip(_grindingEngine?.GetCraneTaskSnapshot());
    }

    /// <summary>
    /// 生成“运行诊断”页面使用的只读内存快照。
    ///
    /// 重要边界：本方法只读取各引擎已经维护的状态字段和展示快照，
    /// 不创建/重连设备、不读取PLC/CNC、不写寄存器、不访问数据库，也不改变任何流程状态。
    /// 某一来源读取失败时只生成一条页面警告，其余来源继续采集。
    /// </summary>
    public RuntimeDiagnosticsSnapshot GetRuntimeDiagnosticsSnapshot()
    {
        var nowUtc = DateTime.UtcNow;
        var items = new List<RuntimeDiagnosticItem>(48);

        // 前端准入结果直接复用全局分线当前正在使用的判断器，页面不另写一套放行逻辑。
        Collect("当前等待", "1号线", "前端准入",
            () => AddFrontReadinessDiagnostic(items, GetFrontDispatchReadiness(1), nowUtc));
        Collect("当前等待", "2号线", "前端准入",
            () => AddFrontReadinessDiagnostic(items, GetFrontDispatchReadiness(2), nowUtc));

        // 只复制前端引擎已刷新的 DeviceStatus。下面每个字段均为内存值，不触发设备通信。
        Collect("设备健康", "1号线前端", "设备状态", () =>
        {
            var engine = _line1Engine;
            var ds = engine?.DeviceStatus;
            bool running = engine?.IsRunning == true;
            bool paused = engine?.IsPaused == true;
            DateTime snapshotAtUtc = ds?.SnapshotAtUtc ?? default;
            AddCommunicationDiagnostic(items, "1号线前端", "机械手1", running, paused, snapshotAtUtc, ds?.Manipulator1Connected == true, nowUtc);
            AddCommunicationDiagnostic(items, "1号线前端", "货叉", running, paused, snapshotAtUtc, ds?.ForkConnected == true, nowUtc);
            AddCommunicationDiagnostic(items, "1号线前端", "总上料架/中转架PLC", running, paused, snapshotAtUtc, ds?.RackConnected == true, nowUtc);
            AddCommunicationDiagnostic(items, "1号线前端", "前天车", running, paused, snapshotAtUtc, ds?.CraneFrontConnected == true, nowUtc);
            AddCommunicationDiagnostic(items, "1号线前端", "双头镗", running, paused, snapshotAtUtc, ds?.BoringConnected == true, nowUtc);
            AddCommunicationDiagnostic(items, "1号线前端", "打号机共享目录", running, paused, snapshotAtUtc, ds?.MarkerConnected == true, nowUtc);
            AddFreshnessDiagnostic(items, "1号线前端", "货叉信号", running, paused,
                ds?.ForkSnapshotValid == true, ds?.ForkSnapshotAtUtc ?? default, nowUtc);
            AddFreshnessDiagnostic(items, "1号线前端", "双头镗信号", running, paused,
                ds?.BoringSnapshotValid == true, ds?.BoringSnapshotAtUtc ?? default, nowUtc);
        });

        Collect("设备健康", "2号线前端", "设备状态", () =>
        {
            var engine = _line2Engine;
            var ds = engine?.DeviceStatus;
            bool running = engine?.IsRunning == true;
            bool paused = engine?.IsPaused == true;
            DateTime snapshotAtUtc = ds?.SnapshotAtUtc ?? default;
            AddCommunicationDiagnostic(items, "2号线前端", "机械手1", running, paused, snapshotAtUtc, ds?.Manipulator1Connected == true, nowUtc);
            AddCommunicationDiagnostic(items, "2号线前端", "货叉", running, paused, snapshotAtUtc, ds?.ForkConnected == true, nowUtc);
            AddCommunicationDiagnostic(items, "2号线前端", "总上料架/中转架PLC", running, paused, snapshotAtUtc, ds?.RackConnected == true, nowUtc);
            AddCommunicationDiagnostic(items, "2号线前端", "前天车", running, paused, snapshotAtUtc, ds?.CraneFrontConnected == true, nowUtc);
            AddCommunicationDiagnostic(items, "2号线前端", "双头镗", running, paused, snapshotAtUtc, ds?.BoringConnected == true, nowUtc);
            AddCommunicationDiagnostic(items, "2号线前端", "打号机共享目录", running, paused, snapshotAtUtc, ds?.MarkerConnected == true, nowUtc);
            AddFreshnessDiagnostic(items, "2号线前端", "货叉信号", running, paused,
                ds?.ForkSnapshotValid == true, ds?.ForkSnapshotAtUtc ?? default, nowUtc);
            AddFreshnessDiagnostic(items, "2号线前端", "双头镗信号", running, paused,
                ds?.BoringSnapshotValid == true, ds?.BoringSnapshotAtUtc ?? default, nowUtc);
        });

        // 后端连接状态同样只来自后端主循环已经维护的 DeviceStatus。
        Collect("设备健康", "1号线后端", "设备状态", () =>
        {
            var engine = _line1RearEngine;
            var ds = engine?.DeviceStatus;
            bool running = engine?.IsRunning == true;
            bool paused = engine?.IsPaused == true;
            DateTime snapshotAtUtc = ds?.SnapshotAtUtc ?? default;
            AddCommunicationDiagnostic(items, "1号线后端", "后天车", running, paused, snapshotAtUtc, ds?.CraneRearConnected == true, nowUtc);
            AddCommunicationDiagnostic(items, "1号线后端", "ST108", running, paused, snapshotAtUtc, ds?.Skew1Connected == true, nowUtc);
            AddCommunicationDiagnostic(items, "1号线后端", "ST109", running, paused, snapshotAtUtc, ds?.Skew2Connected == true, nowUtc);
            AddCommunicationDiagnostic(items, "1号线后端", "ST111", running, paused, snapshotAtUtc, ds?.Skew3Connected == true, nowUtc);
            AddCommunicationDiagnostic(items, "1号线后端", "ST110", running, paused, snapshotAtUtc, ds?.Skew4Connected == true, nowUtc);
            AddCommunicationDiagnostic(items, "1号线后端", "ST112", running, paused, snapshotAtUtc, ds?.Skew5Connected == true, nowUtc);
        });

        Collect("设备健康", "2号线后端", "设备状态", () =>
        {
            var engine = _line2RearEngine;
            var ds = engine?.DeviceStatus;
            bool running = engine?.IsRunning == true;
            bool paused = engine?.IsPaused == true;
            DateTime snapshotAtUtc = ds?.SnapshotAtUtc ?? default;
            AddCommunicationDiagnostic(items, "2号线后端", "后天车", running, paused, snapshotAtUtc, ds?.CraneRearConnected == true, nowUtc);
            AddCommunicationDiagnostic(items, "2号线后端", "ST606", running, paused, snapshotAtUtc, ds?.Skew1Connected == true, nowUtc);
            AddCommunicationDiagnostic(items, "2号线后端", "ST607", running, paused, snapshotAtUtc, ds?.Skew2Connected == true, nowUtc);
            AddCommunicationDiagnostic(items, "2号线后端", "ST608", running, paused, snapshotAtUtc, ds?.Skew3Connected == true, nowUtc);
            AddCommunicationDiagnostic(items, "2号线后端", "ST609", running, paused, snapshotAtUtc, ds?.Skew4Connected == true, nowUtc);
            AddCommunicationDiagnostic(items, "2号线后端", "ST610", running, paused, snapshotAtUtc, ds?.Skew5Connected == true, nowUtc);
        });

        // 中转架快照组合“现有物理信号内存值 + 软件工件身份缓存”，不现场读取PLC。
        Collect("中转架", "1号线", "一致性",
            () => AddRackDiagnostics(items, "1号线", _line1RearEngine?.GetTransferRackSnapshots(), nowUtc));
        Collect("中转架", "2号线", "一致性",
            () => AddRackDiagnostics(items, "2号线", _line2RearEngine?.GetTransferRackSnapshots(), nowUtc));

        // 五台天车始终各显示一行；引擎尚未初始化时明确显示“未初始化”。
        Collect("天车任务", "1号线", "前天车",
            () => AddCraneDiagnostic(items, "1号线", "前天车", _line1Engine?.GetCraneTaskSnapshot(), nowUtc));
        Collect("天车任务", "1号线", "后天车",
            () => AddCraneDiagnostic(items, "1号线", "后天车", _line1RearEngine?.GetCraneTaskSnapshot(), nowUtc));
        Collect("天车任务", "2号线", "前天车",
            () => AddCraneDiagnostic(items, "2号线", "前天车", _line2Engine?.GetCraneTaskSnapshot(), nowUtc));
        Collect("天车任务", "2号线", "后天车",
            () => AddCraneDiagnostic(items, "2号线", "后天车", _line2RearEngine?.GetCraneTaskSnapshot(), nowUtc));
        Collect("天车任务", "研磨", "研磨天车",
            () => AddCraneDiagnostic(items, "研磨", "研磨天车", _grindingEngine?.GetCraneTaskSnapshot(), nowUtc));

        // 仅把引擎已明确标记“需人工补录/确认”的工位加入人工关注区。
        Collect("人工确认", "1号线斜床", "工件身份",
            () => AddManualStationDiagnostics(items, "1号线斜床", _line1RearEngine?.GetSkewStationSnapshots(), nowUtc));
        Collect("人工确认", "2号线斜床", "工件身份",
            () => AddManualStationDiagnostics(items, "2号线斜床", _line2RearEngine?.GetSkewStationSnapshots(), nowUtc));
        Collect("人工确认", "研磨", "工件身份",
            () => AddManualStationDiagnostics(items, "研磨", _grindingEngine?.GetGrinderStationSnapshots(), nowUtc));

        return new RuntimeDiagnosticsSnapshot(items.ToArray(), nowUtc);

        // 单一来源降级：页面每秒刷新，故意不在这里写Console，避免同一故障形成日志风暴。
        void Collect(string section, string scope, string name, Action collect)
        {
            try { collect(); }
            catch { AddSnapshotFailure(items, section, scope, name, nowUtc); }
        }
    }

    private static void AddFrontReadinessDiagnostic(ICollection<RuntimeDiagnosticItem> items,
        FrontDispatchReadinessSnapshot readiness, DateTime nowUtc)
    {
        // HomeViewModel用999表示“未启动/未初始化”的不可选线路；该内部哨兵值不应展示给操作员。
        string detail = readiness.Pressure >= 999
            ? readiness.RejectReason
            : $"{readiness.RejectReason}；当前压力={readiness.Pressure}";
        items.Add(new RuntimeDiagnosticItem(RuntimeDiagnosticCategory.FrontReadiness, "当前等待",
            $"{readiness.Line}号线", "前端准入",
            readiness.CanAccept ? RuntimeDiagnosticLevel.Normal : RuntimeDiagnosticLevel.Waiting,
            readiness.CanAccept ? "可接板" : "暂不可接板",
            detail, nowUtc));
    }

    private static void AddCommunicationDiagnostic(ICollection<RuntimeDiagnosticItem> items,
        string scope, string name, bool engineRunning, bool enginePaused,
        DateTime capturedAtUtc, bool connected, DateTime nowUtc)
    {
        RuntimeDiagnosticLevel level;
        string state;
        string detail;
        if (!engineRunning)
        {
            level = RuntimeDiagnosticLevel.Waiting;
            state = "未启动";
            detail = "对应引擎尚未运行，当前不把无连接状态计为故障";
        }
        else if (enginePaused)
        {
            level = RuntimeDiagnosticLevel.Warning;
            state = "暂停/最后已知";
            detail = $"引擎已暂停；{FormatDisplaySnapshotAge(capturedAtUtc, nowUtc)}，旧连接值不作为当前证据";
        }
        else if (!DisplaySnapshotFreshness.IsFresh(capturedAtUtc, nowUtc))
        {
            level = RuntimeDiagnosticLevel.Warning;
            state = "数据过期";
            detail = $"{FormatDisplaySnapshotAge(capturedAtUtc, nowUtc)}，旧连接值不作为当前证据";
        }
        else
        {
            level = connected ? RuntimeDiagnosticLevel.Normal : RuntimeDiagnosticLevel.Warning;
            state = connected ? "已连接" : "断开";
            detail = connected
                ? $"来自引擎内存快照；{FormatDisplaySnapshotAge(capturedAtUtc, nowUtc)}"
                : $"新鲜内存快照明确为断开；{FormatDisplaySnapshotAge(capturedAtUtc, nowUtc)}";
        }
        items.Add(new RuntimeDiagnosticItem(RuntimeDiagnosticCategory.Communication, "设备健康",
            scope, name, level, state, detail, nowUtc));
    }

    private static void AddFreshnessDiagnostic(ICollection<RuntimeDiagnosticItem> items,
        string scope, string name, bool engineRunning, bool enginePaused,
        bool valid, DateTime capturedAtUtc, DateTime nowUtc)
    {
        if (!engineRunning)
        {
            items.Add(new RuntimeDiagnosticItem(RuntimeDiagnosticCategory.SignalFreshness, "设备健康",
                scope, name, RuntimeDiagnosticLevel.Waiting, "未启动", "对应引擎尚未运行，暂无信号快照", nowUtc));
            return;
        }

        if (enginePaused)
        {
            items.Add(new RuntimeDiagnosticItem(RuntimeDiagnosticCategory.SignalFreshness, "设备健康",
                scope, name, RuntimeDiagnosticLevel.Warning, "暂停/最后已知",
                $"引擎已暂停；{FormatDisplaySnapshotAge(capturedAtUtc, nowUtc)}，旧信号不作为当前证据", nowUtc));
            return;
        }

        TimeSpan age = capturedAtUtc == default ? TimeSpan.MaxValue : nowUtc - capturedAtUtc;
        bool fresh = valid && DisplaySnapshotFreshness.IsFresh(capturedAtUtc, nowUtc);
        string detail = capturedAtUtc == default
            ? "尚未形成成功信号快照"
            : $"最近成功快照：{capturedAtUtc.ToLocalTime():HH:mm:ss.fff}，数据年龄={Math.Max(0, age.TotalSeconds):0.0}秒";
        items.Add(new RuntimeDiagnosticItem(RuntimeDiagnosticCategory.SignalFreshness, "设备健康",
            scope, name, fresh ? RuntimeDiagnosticLevel.Normal : RuntimeDiagnosticLevel.Warning,
            fresh ? "快照有效" : "数据过期/无效", detail, nowUtc));
    }

    private static string FormatDisplaySnapshotAge(DateTime capturedAtUtc, DateTime nowUtc)
    {
        if (capturedAtUtc == default) return "尚未形成整轮状态快照";
        double seconds = Math.Max(0, (nowUtc - capturedAtUtc).TotalSeconds);
        return $"最近快照 {capturedAtUtc.ToLocalTime():HH:mm:ss.fff}，数据年龄={seconds:0.0}秒";
    }

    private static void AddRackDiagnostics(ICollection<RuntimeDiagnosticItem> items, string scope,
        IEnumerable<TransferRackSnapshot>? racks, DateTime nowUtc)
    {
        if (racks == null)
        {
            items.Add(new RuntimeDiagnosticItem(RuntimeDiagnosticCategory.RackConsistency, "中转架",
                scope, "中转架", RuntimeDiagnosticLevel.Waiting, "未初始化",
                "后端引擎尚未初始化，未创建额外设备连接", nowUtc));
            return;
        }

        foreach (var rack in racks)
        {
            string physical = !rack.PhysicalSignalAvailable ? rack.PhysicalSignalUnavailableText : rack.PhysicalHasPlate ? "物理有板" : "物理无板";
            string software = rack.Workpiece == null ? "软件无工件身份" : $"软件={rack.Workpiece.IdentityText}";
            var level = !rack.PhysicalSignalAvailable || rack.HasIdentityMismatch
                ? RuntimeDiagnosticLevel.Warning
                : RuntimeDiagnosticLevel.Normal;
            string state = !rack.PhysicalSignalAvailable ? "信号不可用" : rack.HasIdentityMismatch ? "身份不一致" : "一致";
            items.Add(new RuntimeDiagnosticItem(RuntimeDiagnosticCategory.RackConsistency, "中转架",
                scope, rack.StationCode, level, state, $"{physical}；{software}", nowUtc));

            if (rack.HasIdentityMismatch)
            {
                items.Add(new RuntimeDiagnosticItem(RuntimeDiagnosticCategory.ManualConfirmation, "人工确认",
                    scope, rack.StationCode, RuntimeDiagnosticLevel.ManualConfirmation, "中转架身份不一致",
                    $"{physical}；{software}。请人工核对现场，页面不会自动清理缓存", nowUtc));
            }
        }
    }

    private static void AddCraneDiagnostic(ICollection<RuntimeDiagnosticItem> items, string scope,
        string displayName, CraneTaskSnapshot? snapshot, DateTime nowUtc)
    {
        if (snapshot == null)
        {
            items.Add(new RuntimeDiagnosticItem(RuntimeDiagnosticCategory.CraneTask, "天车任务",
                scope, displayName, RuntimeDiagnosticLevel.Waiting, "未初始化",
                "对应引擎尚未初始化", nowUtc));
            return;
        }

        var level = snapshot.NeedsManualConfirmation
            ? RuntimeDiagnosticLevel.ManualConfirmation
            : RuntimeDiagnosticLevel.Normal;
        string state = snapshot.NeedsManualConfirmation ? "需人工确认" : snapshot.IsActive ? "执行中" : "空闲";
        string workpiece = FormatDiagnosticWorkpiece(snapshot.Workpiece);
        string route = string.IsNullOrWhiteSpace(snapshot.SourceStation) && string.IsNullOrWhiteSpace(snapshot.TargetStation)
            ? ""
            : $"；{snapshot.SourceStation} → {snapshot.TargetStation}";
        string manual = string.IsNullOrWhiteSpace(snapshot.ManualConfirmationText)
            ? ""
            : $"；{snapshot.ManualConfirmationText}";
        items.Add(new RuntimeDiagnosticItem(RuntimeDiagnosticCategory.CraneTask, "天车任务",
            scope, snapshot.CraneName, level, state,
            $"阶段={snapshot.Stage}{route}；{workpiece}{manual}", nowUtc));
    }

    private static void AddManualStationDiagnostics(ICollection<RuntimeDiagnosticItem> items, string scope,
        IEnumerable<ProcessStationSnapshot>? stations, DateTime nowUtc)
    {
        if (stations == null) return;
        foreach (var station in stations.Where(x => x.NeedsManualWorkpiece))
        {
            items.Add(new RuntimeDiagnosticItem(RuntimeDiagnosticCategory.ManualConfirmation, "人工确认",
                scope, station.StationCode, RuntimeDiagnosticLevel.ManualConfirmation, "需确认工件身份",
                $"工位状态={station.State}；{FormatDiagnosticWorkpiece(station.Workpiece)}", nowUtc));
        }
    }

    private static void AddSnapshotFailure(ICollection<RuntimeDiagnosticItem> items, string section,
        string scope, string name, DateTime nowUtc)
    {
        items.Add(new RuntimeDiagnosticItem(RuntimeDiagnosticCategory.SignalFreshness, section,
            scope, name, RuntimeDiagnosticLevel.Warning, "快照暂不可用",
            "读取现有内存快照时发生异常；未触发设备重读、重连或流程控制", nowUtc));
    }

    private static string FormatDiagnosticWorkpiece(WorkpieceDisplaySnapshot? workpiece)
    {
        if (workpiece == null) return "工件=未记录";
        string length = workpiece.Length > 0 ? $"L={workpiece.Length}mm" : "L=未记录";
        return $"{workpiece.IdentityText}，直径={workpiece.Diameter}mm，{length}";
    }

    /// <summary>
    /// 生成“在制工件”页面使用的只读内存快照。
    ///
    /// 安全边界：这里只复制各流程已经维护的工件身份和状态，不读取设备、不重连、
    /// 不写寄存器、不访问数据库，也不把页面判断反馈给任何业务流程。
    /// </summary>
    public InProcessWorkpieceOverviewSnapshot GetInProcessWorkpieceOverviewSnapshot()
    {
        var nowUtc = DateTime.UtcNow;
        var items = new List<InProcessWorkpieceSnapshot>(64);

        Collect("全局FIFO", "全局", () =>
        {
            WorkpieceCache[] queued;
            lock (_frontDispatchLock)
                queued = _frontDispatchQueue.Select(x => x.Workpiece).ToArray();

            foreach (var wp in queued)
            {
                items.Add(new InProcessWorkpieceSnapshot(InProcessWorkpieceKind.GlobalQueue,
                    "全局", "总上料架", "全局FIFO", "等待M800及线路准入",
                    WorkpieceDisplaySnapshot.From(wp), "全局软件任务队列",
                    InProcessWorkpieceStatus.Normal, "尚未写入1/2号线前端缓存", nowUtc));
            }
        });

        // 前端引擎内部自己以短锁复制缓存、在途和天车队列；页面不持有这些锁。
        if (_line1Engine != null)
            Collect("1号线前端", "1号线", () => items.AddRange(_line1Engine.GetInProcessWorkpieceSnapshots()));
        if (_line2Engine != null)
            Collect("2号线前端", "2号线", () => items.AddRange(_line2Engine.GetInProcessWorkpieceSnapshots()));

        if (_line1RearEngine != null)
        {
            Collect("1号线中转架", "1号线", () => AddRackWorkpieces(items, "1号线", "1号线后端",
                _line1RearEngine.GetTransferRackSnapshots(), nowUtc));
            Collect("1号线斜床", "1号线", () => AddProcessWorkpieces(items, "1号线", "1号线后端",
                "斜床软件上下文", _line1RearEngine.GetSkewStationSnapshots(), nowUtc));
            Collect("1号线后天车", "1号线", () => AddCraneWorkpiece(items, "1号线", "1号线后端",
                _line1RearEngine.GetCraneTaskSnapshot(), nowUtc));
        }

        if (_line2RearEngine != null)
        {
            Collect("2号线中转架", "2号线", () => AddRackWorkpieces(items, "2号线", "2号线后端",
                _line2RearEngine.GetTransferRackSnapshots(), nowUtc));
            Collect("2号线斜床", "2号线", () => AddProcessWorkpieces(items, "2号线", "2号线后端",
                "斜床软件上下文", _line2RearEngine.GetSkewStationSnapshots(), nowUtc));
            Collect("2号线后天车", "2号线", () => AddCraneWorkpiece(items, "2号线", "2号线后端",
                _line2RearEngine.GetCraneTaskSnapshot(), nowUtc));
        }

        if (_line1BalancingEngine != null)
            Collect("动平衡", "动平衡", () => items.AddRange(_line1BalancingEngine.GetInProcessWorkpieceSnapshots()));

        if (_grindingEngine != null)
        {
            Collect("研磨缓存", "研磨", () =>
            {
                foreach (var wp in _grindingEngine.GetCachedWorkpiecesSnapshot())
                {
                    items.Add(new InProcessWorkpieceSnapshot(InProcessWorkpieceKind.GrindingCache,
                        "研磨", "研磨", "ST709缓存", "等待分配研磨机",
                        WorkpieceDisplaySnapshot.From(wp), "研磨软件FIFO",
                        InProcessWorkpieceStatus.Normal, "只复制现有研磨缓存", nowUtc));
                }
            });
            Collect("研磨机", "研磨", () => AddProcessWorkpieces(items, "研磨", "研磨",
                "研磨机软件上下文", _grindingEngine.GetGrinderStationSnapshots(), nowUtc));
            Collect("研磨天车", "研磨", () => AddCraneWorkpiece(items, "研磨", "研磨",
                _grindingEngine.GetCraneTaskSnapshot(), nowUtc));
        }

        // 同一已知身份在交接瞬间可能同时存在于上下游两条软件记录中。
        // 页面只用黄色提示核对，绝不能据此自动删除、合并或判定业务异常。
        var repeatedCounts = items.Where(x => x.HasKnownIdentity)
            .GroupBy(x => x.IdentityKey, StringComparer.Ordinal)
            .Where(x => x.Count() > 1)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!item.HasKnownIdentity || !repeatedCounts.TryGetValue(item.IdentityKey, out int count)
                || item.Status != InProcessWorkpieceStatus.Normal)
                continue;

            items[i] = item with
            {
                Status = InProcessWorkpieceStatus.Handoff,
                Detail = $"{item.Detail}；同一身份当前有{count}条软件记录，可能处于正常交接，请结合位置核对"
            };
        }

        return new InProcessWorkpieceOverviewSnapshot(items.ToArray(), nowUtc);

        // 单个来源失败只降级该来源。页面按秒刷新，故意不写Console，避免形成日志风暴。
        void Collect(string location, string scope, Action collect)
        {
            try { collect(); }
            catch
            {
                items.Add(new InProcessWorkpieceSnapshot(InProcessWorkpieceKind.SourceFailure,
                    scope, scope, location, "快照暂不可用", null, "现有内存快照",
                    InProcessWorkpieceStatus.EvidenceUnavailable,
                    "复制该来源的内存状态时发生异常；未触发设备重读、重连或流程控制", nowUtc));
            }
        }
    }

    private static void AddRackWorkpieces(ICollection<InProcessWorkpieceSnapshot> items,
        string filterScope, string flowScope, IEnumerable<TransferRackSnapshot> racks, DateTime nowUtc)
    {
        foreach (var rack in racks)
        {
            // 物理无板且软件也无身份时不是在制工件，不占页面行。
            if (rack.Workpiece == null && (!rack.PhysicalSignalAvailable || !rack.PhysicalHasPlate))
                continue;

            var status = !rack.PhysicalSignalAvailable
                ? InProcessWorkpieceStatus.EvidenceUnavailable
                : rack.HasIdentityMismatch
                    ? InProcessWorkpieceStatus.SignalMismatch
                    : InProcessWorkpieceStatus.Normal;
            string physical = !rack.PhysicalSignalAvailable
                ? rack.PhysicalSignalUnavailableText
                : rack.PhysicalHasPlate ? "物理有板" : "物理无板";
            string detail = rack.HasIdentityMismatch
                ? $"{physical}，软件身份={(rack.Workpiece?.IdentityText ?? "缺失")}；请人工核对，页面不会清理缓存"
                : $"{physical}，软件身份={(rack.Workpiece?.IdentityText ?? "缺失")}";
            if (rack.IsReserved) detail += $"；{rack.ReservationText}";

            items.Add(new InProcessWorkpieceSnapshot(InProcessWorkpieceKind.TransferRack,
                filterScope, flowScope, rack.StationCode, "等待后天车/斜床",
                rack.Workpiece, "物理有板内存信号 + 软件身份缓存", status, detail, nowUtc));
        }
    }

    private static void AddProcessWorkpieces(ICollection<InProcessWorkpieceSnapshot> items,
        string filterScope, string flowScope, string evidence,
        IEnumerable<ProcessStationSnapshot> stations, DateTime nowUtc)
    {
        foreach (var station in stations)
        {
            if (station.Workpiece == null && !station.NeedsManualWorkpiece) continue;
            items.Add(new InProcessWorkpieceSnapshot(InProcessWorkpieceKind.ProcessStation,
                filterScope, flowScope, station.StationCode, station.State,
                station.Workpiece, evidence,
                station.NeedsManualWorkpiece
                    ? InProcessWorkpieceStatus.ManualConfirmation
                    : InProcessWorkpieceStatus.Normal,
                station.NeedsManualWorkpiece
                    ? "设备状态表明存在工件，但软件身份缺失，需要人工确认"
                    : $"状态开始时间={station.StateChangedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}", nowUtc));
        }
    }

    private static void AddCraneWorkpiece(ICollection<InProcessWorkpieceSnapshot> items,
        string filterScope, string flowScope, CraneTaskSnapshot crane, DateTime nowUtc)
    {
        if (!crane.IsActive && !crane.NeedsManualConfirmation) return;
        string route = string.IsNullOrWhiteSpace(crane.SourceStation) && string.IsNullOrWhiteSpace(crane.TargetStation)
            ? "未记录路线"
            : $"{crane.SourceStation} → {crane.TargetStation}";
        bool manual = crane.NeedsManualConfirmation || crane.Workpiece == null;
        items.Add(new InProcessWorkpieceSnapshot(InProcessWorkpieceKind.CraneTask,
            filterScope, flowScope, crane.CraneName, crane.Stage,
            crane.Workpiece, "天车当前动作展示快照",
            manual ? InProcessWorkpieceStatus.ManualConfirmation : InProcessWorkpieceStatus.Normal,
            manual && !string.IsNullOrWhiteSpace(crane.ManualConfirmationText)
                ? $"{route}；{crane.ManualConfirmationText}"
                : route, nowUtc));
    }

    private void ApplyProcessTooltips(IEnumerable<ProcessStationSnapshot>? snapshots)
    {
        if (snapshots == null) return;
        foreach (var snapshot in snapshots)
            if (StationCards.TryGetValue(snapshot.StationCode, out var card))
                card.TooltipText = FormatProcessTooltip(snapshot);
    }

    private static string FormatProcessTooltip(ProcessStationSnapshot snapshot)
    {
        if (snapshot.Workpiece == null)
            return snapshot.NeedsManualWorkpiece
                ? $"{snapshot.StationCode} — {snapshot.State}\n需人工补录工件信息"
                : $"{snapshot.StationCode} — {snapshot.State}\n当前无工件";

        var wp = snapshot.Workpiece;
        var length = wp.Length > 0 ? $"长度：{wp.Length}mm" : "长度：未记录";
        var process = string.IsNullOrWhiteSpace(wp.Process) ? "工艺：未记录" : $"工艺：{wp.Process}";
        return $"{snapshot.StationCode} — {snapshot.State}\n工件：{wp.IdentityText}\n直径：{wp.Diameter}mm    {length}\n{process}\n开始时间：{snapshot.StateChangedAtUtc.ToLocalTime():HH:mm:ss}\n当前状态持续：{FormatDuration(snapshot.StateChangedAtUtc)}";
    }

    private static string FormatTransferRackTooltip(IEnumerable<TransferRackSnapshot>? snapshots)
    {
        if (snapshots == null) return "中转架数据暂不可用";
        return string.Join(Environment.NewLine + Environment.NewLine, snapshots.Select(snapshot =>
        {
            if (!snapshot.PhysicalSignalAvailable) return $"{snapshot.StationCode}：{snapshot.PhysicalSignalUnavailableText}";
            if (snapshot.Workpiece == null)
                return snapshot.PhysicalHasPlate ? $"{snapshot.StationCode}：物理有板，但软件身份缺失" : $"{snapshot.StationCode}：物理无板";
            var wp = snapshot.Workpiece;
            var prefix = snapshot.HasIdentityMismatch ? "物理/软件状态不一致" : (snapshot.PhysicalHasPlate ? "物理有板" : "软件缓存存在");
            string reservation = snapshot.IsReserved ? $"\n{snapshot.ReservationText}" : string.Empty;
            return $"{snapshot.StationCode}：{prefix}\n工件：{wp.IdentityText}\nD={wp.Diameter}mm  L={wp.Length}mm{reservation}";
        }));
    }

    private static string FormatProcessGroupTooltip(IEnumerable<ProcessStationSnapshot>? snapshots, string title)
    {
        if (snapshots == null) return $"{title}数据暂不可用";
        return string.Join(Environment.NewLine + Environment.NewLine, snapshots.Select(FormatProcessTooltip));
    }

    private static string FormatCraneTooltip(CraneTaskSnapshot? snapshot)
    {
        if (snapshot == null) return "流程任务数据暂不可用";
        if (!snapshot.IsActive) return $"{snapshot.CraneName} — 空闲\n当前无流程任务";
        var wp = snapshot.Workpiece == null ? "工件：需人工确认" : $"工件：{snapshot.Workpiece.IdentityText}\n直径：{snapshot.Workpiece.Diameter}mm  长度：{snapshot.Workpiece.Length}mm";
        var manual = snapshot.NeedsManualConfirmation ? $"\n等待人工确认：{snapshot.ManualConfirmationText}" : string.Empty;
        return $"{snapshot.CraneName} — 执行中\n{wp}\n阶段：{snapshot.Stage}\n来源：{snapshot.SourceStation}    目标：{snapshot.TargetStation}{manual}";
    }

    private static string FormatDuration(DateTime changedAtUtc)
    {
        var elapsed = DateTime.UtcNow - changedAtUtc;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}时{elapsed.Minutes}分{elapsed.Seconds}秒"
            : $"{elapsed.Minutes}分{elapsed.Seconds}秒";
    }

    private static string ShortStatusText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "--") return "连接/读取失败";
        return text.Length <= 80 ? text : text[..80];
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

    private static void SetLine2DropRackHandshakeCard(Dictionary<string, StationCardViewModel> cards,
        string code, bool connected, bool canPlace, bool placeDone, bool canPick, bool pickDone, bool cachePresent,
        string canPlaceName, string placeDoneName, string canPickName, string pickDoneName, string ipText)
    {
        if (!cards.TryGetValue(code, out var card)) return;

        card.ConnectedBrush = connected ? Brushes.LimeGreen : Brushes.Gray;
        card.IpText = connected ? ipText : "未连接";
        if (!connected)
        {
            card.Status1 = "读取失败";
            card.Status1Brush = Brushes.Red;
            card.Status2 = $"{canPlaceName}/{placeDoneName}/{canPickName}/{pickDoneName}快照失败";
            return;
        }

        string state;
        Brush brush;
        if (canPick && cachePresent)
        {
            state = "可取料";
            brush = Brushes.Orange;
        }
        else if (pickDone)
        {
            state = "已取完";
            brush = Brushes.Green;
        }
        else if (canPick && !cachePresent)
        {
            state = "异常:无缓存";
            brush = Brushes.Red;
        }
        else if (cachePresent)
        {
            state = "等取料允许";
            brush = Brushes.Orange;
        }
        else if (canPlace)
        {
            state = "可放料";
            brush = Brushes.Green;
        }
        else if (placeDone)
        {
            state = "已放料";
            brush = Brushes.Orange;
        }
        else
        {
            state = "等待PLC";
            brush = Brushes.Gray;
        }

        card.Status1 = state;
        card.Status1Brush = brush;
        card.Status2 =
            $"{canPlaceName}/{placeDoneName}={To01(canPlace)}/{To01(placeDone)} {canPickName}/{pickDoneName}={To01(canPick)}/{To01(pickDone)} 缓存={(cachePresent ? "有" : "无")}";
    }

    private void SyncBalancingCards(Dictionary<string, StationCardViewModel> cards)
    {
        var engine = _line1BalancingEngine;
        if (engine?.IsRunning == true)
        {
            bool mc63 = engine.Mc63Connected && engine.Mc63SnapshotValid;
            SetPlateCard(cards, "ST019", mc63, engine.M817HasPlate, $"M817 M2={(engine.M2Busy ? "忙" : "闲")}", "192.168.2.63:9000");
            SetLine2DropRackHandshakeCard(cards, "ST020", mc63,
                engine.M818CanPlace, engine.M819PlaceDone, engine.M823CanPick, engine.M824PickDone,
                engine.M818CachePresent, "M818", "M819", "M823", "M824", "192.168.2.63:9000");
            SetLine2DropRackHandshakeCard(cards, "ST021", mc63,
                engine.M820CanPlace, engine.M821PlaceDone, engine.M825CanPick, engine.M826PickDone,
                engine.M821CachePresent, "M820", "M821", "M825", "M826", "192.168.2.63:9000");

            bool mc65 = engine.Mc65Connected && engine.Mc65SnapshotValid;
            SetReadyCard(cards, "ST008", mc65, engine.M710CanPlace, "可放料", "不可放料", $"M710 M2={(engine.M2Busy ? "忙" : "闲")}", "192.168.2.65:9000");
            SetPlateCard(cards, "ST009", mc65, engine.M700HasPlate, $"M700 M3={(engine.M3Busy ? "忙" : "闲")}", "192.168.2.65:9000");
            SetReadyCard(cards, "ST010", mc65, engine.M720CanPlace, "可放料", "不可放料", $"M720 M3={(engine.M3Busy ? "忙" : "闲")}", "192.168.2.65:9000");
            return;
        }

        // 动平衡引擎未运行时，MC65三张卡没有新鲜来源，必须清掉旧状态。
        MarkCardsNotStarted(cards, "ST008", "ST009", "ST010");

        var line1 = _line1Engine is { IsRunning: true } ? _line1Engine.DeviceStatus : null;
        if (line1 != null)
        {
            SetPlateCard(cards, "ST019", line1.RackConnected && line1.M817SnapshotValid,
                line1.M817_HasPlate, line1.M817SnapshotValid ? "M817" : "M817快照失败", "192.168.2.63:9000");
        }
        else
        {
            MarkCardsNotStarted(cards, "ST019");
        }

        var line2 = _line2Engine is { IsRunning: true } ? _line2Engine.DeviceStatus : null;
        if (line2 != null)
        {
            bool dropRackSnapshotOk = line2.RackConnected && line2.DropRackSnapshotValid;
            SetLine2DropRackHandshakeCard(cards, "ST020", dropRackSnapshotOk,
                line2.M818CanPlace, line2.M819PlaceDone, line2.M823CanPick, line2.M824PickDone,
                engine?.M818CachePresent == true, "M818", "M819", "M823", "M824", "192.168.2.63:9000");
            SetLine2DropRackHandshakeCard(cards, "ST021", dropRackSnapshotOk,
                line2.M820CanPlace, line2.M821PlaceDone, line2.M825CanPick, line2.M826PickDone,
                engine?.M821CachePresent == true, "M820", "M821", "M825", "M826", "192.168.2.63:9000");
        }
        else
        {
            MarkCardsNotStarted(cards, "ST020", "ST021");
        }
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
            bool m721PlaceDone = (raw & (1 << 1)) != 0;

            if (cards.TryGetValue("ST710", out var c710))
            {
                c710.ConnectedBrush = Brushes.LimeGreen;
                c710.Status1 = m720CanPlace ? "无板/允许放料" : "不可放料";
                c710.Status1Brush = m720CanPlace ? Brushes.Green : Brushes.Orange;
                c710.Status2 = $"M720={(m720CanPlace ? 1 : 0)} M721={(m721PlaceDone ? 1 : 0)}";
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
    private readonly object _lineSafetyPopupLock = new();
    private bool _line1SafetyPopupShown;
    private bool _line2SafetyPopupShown;
    /// <summary>1号线是否在运行</summary>
    public bool IsLine1Running { get => _isLine1Running; set { if (SetField(ref _isLine1Running, value)) OnPropertyChanged(nameof(Line1ToggleText)); } }

    /// <summary>1号线启动/暂停按钮文本</summary>
    public string Line1ToggleText => _isLine1Running ? "暂停1号线" : "启动1号线";

    /// <summary>1号线缓存工件数量</summary>
    public int Line1CachedCount => _line1Engine?.CachedCount ?? 0;
    public string Line1CacheDetail => FormatCacheDetail(_line1Engine?.GetCachedWorkpiecesSnapshot());

    /// <summary>总上料架前的全局待派发队列数量。任务先进入这里, M800到位后再唯一派发到1/2号线。</summary>
    public int FrontDispatchCachedCount { get { lock (_frontDispatchLock) return _frontDispatchQueue.Count; } }

    // ── 2号线运行状态 ────────────────────────────────────────────────
    private bool _isLine2Running;
    public bool IsLine2Running { get => _isLine2Running; set { if (SetField(ref _isLine2Running, value)) OnPropertyChanged(nameof(Line2ToggleText)); } }
    public string Line2ToggleText => _isLine2Running ? "暂停2号线" : "启动2号线";
    public int Line2CachedCount => _line2Engine?.CachedCount ?? 0;
    public string Line2CacheDetail => FormatCacheDetail(_line2Engine?.GetCachedWorkpiecesSnapshot());

    private static string FormatCacheDetail(IEnumerable<WorkpieceCache>? workpieces)
    {
        var firstTwo = workpieces?.Take(2).ToArray() ?? Array.Empty<WorkpieceCache>();
        if (firstTwo.Length == 0) return "无缓存";

        return string.Join(Environment.NewLine, firstTwo.Select(wp => wp.Length > 0
            ? $"D={wp.Diameter}mm  L={wp.Length}mm"
            : $"D={wp.Diameter}mm"));
    }

    // ── 动平衡引擎 ────────────────────────────────────────────────
    private bool _isBalancingRunning;
    private bool _balancingSafetyPopupShown;
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
            if (!await EnsureCranePositionsReadyForStartAsync(
                    new[] { (1, "1号线前天车"), (2, "1号线后天车") }, "1号线引擎启动/恢复前"))
                return;
            if (!EnsureNoPendingManualActionsForLine(1))
                return;

            lock (_lineSafetyPopupLock) _line1SafetyPopupShown = false;

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
            if (!await EnsureCranePositionsReadyForStartAsync(
                    new[] { (3, "2号线前天车"), (4, "2号线后天车") }, "2号线引擎启动/恢复前"))
                return;
            if (!EnsureNoPendingManualActionsForLine(2))
                return;

            lock (_lineSafetyPopupLock) _line2SafetyPopupShown = false;

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

    /// <summary>
    /// 引擎启动/恢复前读取天车实时显示坐标。第一次全零后延时500ms复核，
    /// 只有连续两次全零才阻止启动；读取失败仍交由原通信异常流程处理。
    /// </summary>
    private async Task<bool> EnsureCranePositionsReadyForStartAsync(
        IEnumerable<(int CraneNo, string CraneName)> cranes, string context)
    {
        foreach (var (craneNo, craneName) in cranes)
        {
            try
            {
                var crane = _craneCache.GetOrCreateService(craneNo);
                var alarm = await CraneZeroPositionGuard.CheckAsync(
                    crane, craneNo, craneName, context, CancellationToken.None);
                if (alarm == null) continue;

                HandleCraneZeroPositionAlarm(alarm);
                return false;
            }
            catch (Exception ex)
            {
                // 本保护只识别“成功读取且连续两次全零”；通信失败不能伪装成坐标丢失。
                Console.WriteLine($"[天车坐标保护] [{craneName}] {context} 读取异常，交由现有通信异常流程处理: {ex.Message}");
            }
        }
        return true;
    }

    /// <summary>
    /// 安全告警的弹窗和引擎暂停可能发生在不同线程；恢复前以动作账本作最后一道门禁。
    /// 不在此处猜测性清理快照，必须先走对应引擎的人工结案入口。
    /// </summary>
    private bool EnsureNoPendingManualActionsForLine(int line)
    {
        string linePrefix = $"{line}号线";
        FlowActionSnapshot[] pending = FlowActionManualRegistry.Snapshot()
            .Where(item => item.ManualConfirmationRequired &&
                           item.FlowScope.StartsWith(linePrefix, StringComparison.Ordinal))
            .ToArray();
        if (pending.Length == 0)
            return true;

        string actions = string.Join("\n", pending.Select(item =>
            $"- {item.OperationId}：{item.FlowScope} / {item.WorkpieceIdentity} / {item.PauseReason}"));
        string message = $"{line}号线仍有 {pending.Length} 条物理动作等待人工确认，禁止启动或恢复。\n\n" +
            actions + "\n\n请先在应急中心按现场事实完成对应动作的人工结案；不能通过再次点击启动绕过此门禁。";
        Console.WriteLine($"[HomeViewModel] ⚠ {message}");
        MessageBox.Show(message, $"{line}号线待人工确认 - 禁止恢复",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>按天车归属暂停对应引擎，并在主页面线程显示明确告警。</summary>
    private void HandleCraneZeroPositionAlarm(CraneZeroPositionAlarm alarm)
    {
        // 先在当前线程立即关掉派发门，UI调度延迟不能留下继续派发的窗口。
        if (alarm.CraneNo is 1 or 2)
        {
            _line1Engine?.Pause();
            _line1RearEngine?.Pause();
        }
        else if (alarm.CraneNo is 3 or 4)
        {
            _line2Engine?.Pause();
            _line2RearEngine?.Pause();
        }
        else if (alarm.CraneNo == _cfg.Grinding.CraneNo)
        {
            _grindingEngine?.PauseForCraneZeroPosition();
        }

        string message =
            $"{alarm.CraneName}连续两次读取到XYZ全为0，疑似天车断电后坐标丢失。\n\n" +
            $"检查位置：{alarm.Context}\n" +
            $"第一次：{alarm.First}\n第二次：{alarm.Second}\n\n" +
            "对应引擎已暂停，当前任务和缓存未清除。请确认天车坐标恢复正常后，再点击启动继续。";
        string scope = alarm.CraneNo is 1 or 2 ? "1号线"
            : alarm.CraneNo is 3 or 4 ? "2号线"
            : "研磨";
        _attentionEvents.Record(AttentionEventKind.SafetyAlarm, scope,
            $"{alarm.CraneName}坐标保护", message);

        void UpdateUiAndShowAlarm()
        {
            if (alarm.CraneNo is 1 or 2) IsLine1Running = false;
            else if (alarm.CraneNo is 3 or 4) IsLine2Running = false;
            else if (alarm.CraneNo == _cfg.Grinding.CraneNo) IsGrindingRunning = false;

            Console.WriteLine($"[HomeViewModel] ⚠ {message.Replace(Environment.NewLine, " | ")}");
            MessageBox.Show(message, "天车坐标异常 - 引擎已暂停",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) UpdateUiAndShowAlarm();
        else dispatcher.BeginInvoke((Action)UpdateUiAndShowAlarm);
    }

    /// <summary>后天车无法完成退磁/回升时，立即暂停对应整线并在主页面显示安全告警。</summary>
    private void HandleLineSafetyAlarm(int line, string source, string message, bool recordLegacyEvent = true)
    {
        // 先同步关闭前后端派发门；不能等待UI线程调度后再暂停。
        if (line == 1)
        {
            _line1Engine?.Pause();
            _line1RearEngine?.Pause();
        }
        else if (line == 2)
        {
            _line2Engine?.Pause();
            _line2RearEngine?.Pause();
        }

        // 同一次停机可能由内层catch、外层catch和finally分别报告；所有异常均记录，仅弹窗去重。
        bool popupAlreadyShown;
        lock (_lineSafetyPopupLock)
        {
            if (line == 1)
            {
                popupAlreadyShown = _line1SafetyPopupShown;
                if (!popupAlreadyShown) _line1SafetyPopupShown = true;
            }
            else if (line == 2)
            {
                popupAlreadyShown = _line2SafetyPopupShown;
                if (!popupAlreadyShown) _line2SafetyPopupShown = true;
            }
            else popupAlreadyShown = false;
        }

        // X11三次失败会在finally生成完整结构化事件；该专用入口只跳过重复Legacy记录，暂停和弹窗完全相同。
        if (recordLegacyEvent)
            _attentionEvents.Record(AttentionEventKind.SafetyAlarm, $"{line}号线", source, message);
        if (popupAlreadyShown) return;

        void UpdateUiAndShowAlarm()
        {
            if (line == 1) IsLine1Running = false;
            else if (line == 2) IsLine2Running = false;

            Console.WriteLine($"[HomeViewModel] ⚠ {source}: {message}");
            MessageBox.Show(message, $"{line}号线{source} - 引擎已暂停",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) UpdateUiAndShowAlarm();
        else dispatcher.BeginInvoke((Action)UpdateUiAndShowAlarm);
    }

    /// <summary>
    /// 机械手1是1/2号线共用设备。若其在两个安全端点之间停止或位置读不到，
    /// 仅暂停发生异常的一条线仍可能让另一条线取得共享锁并继续发运动命令，因此必须同步暂停两条线。
    /// 已经打开的货叉状态机不回Idle、不清工件身份，避免恢复后重复派发同一块板。
    /// </summary>
    private void HandleSharedManipulatorSafetyAlarm(string message)
    {
        // 先在当前后台线程同步关闭四个派发门，再调度UI弹窗；不能把安全暂停延后到Dispatcher执行。
        _line1Engine?.Pause();
        _line1RearEngine?.Pause();
        _line2Engine?.Pause();
        _line2RearEngine?.Pause();
        _attentionEvents.Record(AttentionEventKind.SafetyAlarm, "全局", "共享机械手1安全异常", message);

        void UpdateUiAndShowAlarm()
        {
            IsLine1Running = false;
            IsLine2Running = false;
            Console.WriteLine($"[HomeViewModel] ⚠ 共享机械手1安全异常: {message}");
            MessageBox.Show(message, "共享机械手1安全异常 - 1/2号线已暂停",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) UpdateUiAndShowAlarm();
        else dispatcher.BeginInvoke((Action)UpdateUiAndShowAlarm);
    }

    /// <summary>X11连续无板但已安全退磁回升：只提示一次，不改变引擎运行状态。</summary>
    private void HandleRearCraneWarning(int line, string message)
    {
        _attentionEvents.Record(AttentionEventKind.Warning, $"{line}号线", "后天车取料警告", message);

        void ShowWarning()
        {
            Console.WriteLine($"[HomeViewModel] ⚠ {line}号线后天车取料警告: {message}");
            MessageBox.Show(message, $"{line}号线后天车取料警告 - 引擎继续运行",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) ShowWarning();
        else dispatcher.BeginInvoke((Action)ShowWarning);
    }

    /// <summary>动平衡/研磨最终安全异常：每次恢复运行前只显示第一条。</summary>
    private void HandleStandaloneSafetyAlarm(string engine, string message)
    {
        if (engine == "动平衡") _line1BalancingEngine?.Pause();
        else if (engine == "研磨") _grindingEngine?.PauseForCraneZeroPosition();

        // 所有异常均记录，仅弹窗去重。
        bool popupAlreadyShown;
        lock (_lineSafetyPopupLock)
        {
            if (engine == "动平衡")
            {
                popupAlreadyShown = _balancingSafetyPopupShown;
                if (!popupAlreadyShown) _balancingSafetyPopupShown = true;
            }
            else if (engine == "研磨")
            {
                popupAlreadyShown = _grindingSafetyPopupShown;
                if (!popupAlreadyShown) _grindingSafetyPopupShown = true;
            }
            else popupAlreadyShown = false;
        }

        _attentionEvents.Record(AttentionEventKind.SafetyAlarm, engine, $"{engine}流程安全异常", message);
        if (popupAlreadyShown) return;

        void ShowAlarm()
        {
            if (engine == "动平衡") IsBalancingRunning = false;
            else if (engine == "研磨") IsGrindingRunning = false;
            Console.WriteLine($"[HomeViewModel] ⚠ {engine}流程安全异常: {message}");
            MessageBox.Show(message, $"{engine}流程异常 - 引擎已暂停",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) ShowAlarm();
        else dispatcher.BeginInvoke((Action)ShowAlarm);
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
            lock (_lineSafetyPopupLock) _balancingSafetyPopupShown = false;
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

    public string GetSkewEmergencyInfo(int line, string bedCode)
    {
        return line switch
        {
            1 => _line1RearEngine?.GetSkewEmergencyInfo(bedCode) ?? "1号线后端引擎未初始化",
            2 => _line2RearEngine?.GetSkewEmergencyInfo(bedCode) ?? "2号线后端引擎未初始化",
            _ => $"无效线体: {line}"
        };
    }

    public string EmergencyClearSkewBed(int line, string bedCode)
    {
        return line switch
        {
            1 => _line1RearEngine?.EmergencyClearSkewBed(bedCode) ?? "1号线后端引擎未初始化",
            2 => _line2RearEngine?.EmergencyClearSkewBed(bedCode) ?? "2号线后端引擎未初始化",
            _ => $"无效线体: {line}"
        };
    }

    public Task<string> EmergencyClearSkewBedAsync(int line, string bedCode, bool skipDeviceClear, CancellationToken ct = default)
    {
        return line switch
        {
            1 => _line1RearEngine?.EmergencyClearSkewBedAsync(bedCode, skipDeviceClear, IsLine1Running, ct)
                 ?? Task.FromResult("1号线后端引擎未初始化"),
            2 => _line2RearEngine?.EmergencyClearSkewBedAsync(bedCode, skipDeviceClear, IsLine2Running, ct)
                 ?? Task.FromResult("2号线后端引擎未初始化"),
            _ => Task.FromResult($"无效线体: {line}")
        };
    }

    /// <summary>人工确认来源位异常后的唯一账本结案入口；不会发送设备动作或自动恢复线路。</summary>
    public string ResolveRearSourceManualReservation(int line, string rackCode, FlowActionManualResolution resolution)
    {
        return line switch
        {
            1 => _line1RearEngine?.ResolveManualSourceReservation(rackCode, resolution) ?? "1号线后端引擎未初始化",
            2 => _line2RearEngine?.ResolveManualSourceReservation(rackCode, resolution) ?? "2号线后端引擎未初始化",
            _ => $"无效线体: {line}"
        };
    }

    public string ResolveRearLoadTargetPending(int line, string operationId) => line switch
    {
        1 => _line1RearEngine?.ResolveManualLoadTargetPending(operationId) ?? "1号线后端引擎未初始化",
        2 => _line2RearEngine?.ResolveManualLoadTargetPending(operationId) ?? "2号线后端引擎未初始化",
        _ => $"无效线体: {line}"
    };

    /// <summary>后天车斜床下料动作的人工账本结案；不自动恢复后端引擎。</summary>
    public string ResolveRearUnloadManualAction(int line, string operationId, FlowActionManualResolution resolution)
    {
        return line switch
        {
            1 => _line1RearEngine?.ResolveManualUnloadAction(operationId, resolution) ?? "1号线后端引擎未初始化",
            2 => _line2RearEngine?.ResolveManualUnloadAction(operationId, resolution) ?? "2号线后端引擎未初始化",
            _ => $"无效线体: {line}"
        };
    }

    /// <summary>人工确认后补后天车目标缓存和PLC握手；成功前不清原斜床账本。</summary>
    public Task<string> ResolveRearUnloadTargetPendingAsync(int line, string operationId, CancellationToken ct = default) => line switch
    {
        1 => _line1RearEngine?.ResolveManualUnloadTargetPendingAsync(operationId, ct)
             ?? Task.FromResult("1号线后端引擎未初始化"),
        2 => _line2RearEngine?.ResolveManualUnloadTargetPendingAsync(operationId, ct)
             ?? Task.FromResult("2号线后端引擎未初始化"),
        _ => Task.FromResult($"无效线体: {line}")
    };

    /// <summary>
    /// 应急中心的只读动作账本。此处绝不据此发送动作或自动恢复引擎；
    /// 现场确认后的缓存/Pending/任务牌结案仍必须调用各引擎的专用入口。
    /// </summary>
    public string GetPendingManualFlowActions()
    {
        IReadOnlyList<FlowActionSnapshot> snapshots = FlowActionManualRegistry.Snapshot();
        if (snapshots.Count == 0)
            return "当前没有因物理命令异常而等待人工确认的搬运动作。\n\n" +
                   "注意：没有待确认动作不等于设备现场安全；仍需按各设备实时诊断确认。";

        var builder = new StringBuilder();
        builder.AppendLine($"待人工确认动作：{snapshots.Count} 条（只读账本，不执行设备命令）");
        builder.AppendLine("请先按动作号、工件、最后坐标和X11核对现场；完成对应引擎的专用应急结案后，条目才会消失。");
        foreach (FlowActionSnapshot item in snapshots)
        {
            builder.AppendLine();
            builder.AppendLine($"动作号: {item.OperationId}");
            builder.AppendLine($"流程/设备: {item.FlowScope} / {item.DeviceName}");
            builder.AppendLine($"工件: {item.WorkpieceIdentity}");
            builder.AppendLine($"来源 → 目标: {item.Source} → {item.Target}；来源账本={item.SourceCacheKey}");
            builder.AppendLine($"步骤/命令: {item.Step} / {item.CommandState}");
            builder.AppendLine($"工件归属/结论: {item.Ownership} / {item.Disposition}");
            builder.AppendLine($"目标坐标: {FormatActionPosition(item.TargetPosition)}；最后坐标: {FormatActionPosition(item.LastKnownPosition)}");
            builder.AppendLine($"X11: {FormatNullableBool(item.X11)}；磁铁: {FormatNullableBool(item.MagnetOn)}");
            builder.AppendLine($"异常点锁: {(item.HeldLocks.Count == 0 ? "未登记" : string.Join("、", item.HeldLocks))}");
            builder.AppendLine($"暂停原因: {item.PauseReason}");
        }
        return builder.ToString();
    }

    /// <summary>研磨天车四种人工结论的唯一页面入口；不自动恢复引擎。</summary>
    public string ResolveGrindingManualAction(string operationId, FlowActionManualResolution resolution) =>
        _grindingEngine?.ResolveManualAction(operationId, resolution) ?? "研磨引擎未初始化";

    /// <summary>机械手2/3四种人工结论的唯一页面入口；不自动恢复引擎。</summary>
    public string ResolveBalancingManualAction(string operationId, FlowActionManualResolution resolution) =>
        _line1BalancingEngine?.ResolveManualAction(operationId, resolution) ?? "动平衡引擎未初始化";

    /// <summary>仅补前天车“工件已在中转架但缓存未闭环”的目标待交接账本。</summary>
    public string ResolveFrontCraneTargetPending(int line, string operationId) => line switch
    {
        1 => _line1Engine?.ResolveFrontCraneTargetPending(operationId) ?? "1号线前端引擎未初始化",
        2 => _line2Engine?.ResolveFrontCraneTargetPending(operationId) ?? "2号线前端引擎未初始化",
        _ => $"无效线体: {line}"
    };

    /// <summary>前天车非目标待交接的受限人工结案；不会触碰货叉/双头镗状态。</summary>
    public string ResolveFrontCraneManualAction(int line, string operationId, FlowActionManualResolution resolution) => line switch
    {
        1 => resolution switch
        {
            FlowActionManualResolution.StillAtSource => _line1Engine?.ResolveFrontCraneStillAtSource(operationId) ?? "1号线前端引擎未初始化",
            FlowActionManualResolution.RemovedManually => _line1Engine?.ResolveFrontCraneRemovedManually(operationId) ?? "1号线前端引擎未初始化",
            FlowActionManualResolution.OnCarrier => "前天车确认天车持件后必须保留人工任务牌和动作快照，等待现场移走或转入真实目标缓存结案。",
            _ => "前天车该结论不支持此入口。"
        },
        2 => resolution switch
        {
            FlowActionManualResolution.StillAtSource => _line2Engine?.ResolveFrontCraneStillAtSource(operationId) ?? "2号线前端引擎未初始化",
            FlowActionManualResolution.RemovedManually => _line2Engine?.ResolveFrontCraneRemovedManually(operationId) ?? "2号线前端引擎未初始化",
            FlowActionManualResolution.OnCarrier => "前天车确认天车持件后必须保留人工任务牌和动作快照，等待现场移走或转入真实目标缓存结案。",
            _ => "前天车该结论不支持此入口。"
        },
        _ => $"无效线体: {line}"
    };

    private static string FormatActionPosition(FlowActionPosition position) =>
        $"X={position.X?.ToString() ?? "未知"}, Y={position.Y?.ToString() ?? "未知"}, Z={position.Z?.ToString() ?? "未知"}";

    private static string FormatNullableBool(bool? value) => value switch
    {
        true => "1/是",
        false => "0/否",
        null => "未知"
    };

    /// <summary>读取1/2号线前端在途实时诊断；只读，不自动暂停或修改状态。</summary>
    public Task<string> GetFrontEmergencyInfoAsync(int line, CancellationToken ct = default)
    {
        return line switch
        {
            1 => _line1Engine?.GetFrontEmergencyInfoAsync(ct) ?? Task.FromResult("1号线前端引擎未初始化"),
            2 => _line2Engine?.GetFrontEmergencyInfoAsync(ct) ?? Task.FromResult("2号线前端引擎未初始化"),
            _ => Task.FromResult($"无效线体: {line}")
        };
    }

    /// <summary>
    /// 查询普通双头镗清零是否会破坏前端在途阶段。
    /// 返回null表示没有软件在途阻断；这不代表设备物理状态安全。
    /// </summary>
    public string? GetBoringClearBlockReason(int line)
    {
        return line switch
        {
            1 => _line1Engine?.GetBoringClearBlockReason(),
            2 => _line2Engine?.GetBoringClearBlockReason(),
            _ => $"无效线体: {line}"
        };
    }

    /// <summary>暂停对应整线后，尝试安全打开“板已在货叉”的当前任务门闩。</summary>
    public async Task<string> EmergencyContinueFrontPlateAsync(int line, CancellationToken ct = default)
    {
        PauseWholeLineForFrontEmergency(line);
        return line switch
        {
            1 => _line1Engine == null ? "1号线前端引擎未初始化" : await _line1Engine.EmergencyContinuePlateOnForkAsync(ct: ct),
            2 => _line2Engine == null ? "2号线前端引擎未初始化" : await _line2Engine.EmergencyContinuePlateOnForkAsync(ct: ct),
            _ => $"无效线体: {line}"
        };
    }

    /// <summary>机械手1已在货叉待交接的动作账本结案；复用实时安全复核和M801补写。</summary>
    public async Task<string> ResolveManipulator1TargetPendingAsync(int line, string operationId, CancellationToken ct = default)
    {
        PauseWholeLineForFrontEmergency(line);
        return line switch
        {
            1 => _line1Engine == null ? "1号线前端引擎未初始化" : await _line1Engine.EmergencyContinuePlateOnForkAsync(operationId, ct),
            2 => _line2Engine == null ? "2号线前端引擎未初始化" : await _line2Engine.EmergencyContinuePlateOnForkAsync(operationId, ct),
            _ => $"无效线体: {line}"
        };
    }

    /// <summary>机械手1动作账本的非目标结案；不会发送运动或PLC命令。</summary>
    public string ResolveManipulator1ManualAction(int line, string operationId, FlowActionManualResolution resolution)
    {
        PauseWholeLineForFrontEmergency(line);
        return line switch
        {
            1 => resolution switch
            {
                FlowActionManualResolution.StillAtSource => _line1Engine?.ResolveManipulator1StillAtSource(operationId) ?? "1号线前端引擎未初始化",
                FlowActionManualResolution.OnCarrier => _line1Engine?.ResolveManipulator1OnCarrier(operationId) ?? "1号线前端引擎未初始化",
                FlowActionManualResolution.RemovedManually => _line1Engine?.ResolveManipulator1RemovedManually(operationId) ?? "1号线前端引擎未初始化",
                _ => "机械手1目标待交接必须使用带实时复核的专用结案入口。"
            },
            2 => resolution switch
            {
                FlowActionManualResolution.StillAtSource => _line2Engine?.ResolveManipulator1StillAtSource(operationId) ?? "2号线前端引擎未初始化",
                FlowActionManualResolution.OnCarrier => _line2Engine?.ResolveManipulator1OnCarrier(operationId) ?? "2号线前端引擎未初始化",
                FlowActionManualResolution.RemovedManually => _line2Engine?.ResolveManipulator1RemovedManually(operationId) ?? "2号线前端引擎未初始化",
                _ => "机械手1目标待交接必须使用带实时复核的专用结案入口。"
            },
            _ => $"无效线体: {line}"
        };
    }

    /// <summary>暂停对应整线后，执行前端当前工件的设备优先/仅软件两级作废。</summary>
    public async Task<string> EmergencyDiscardFrontCurrentAsync(int line, bool skipDeviceClear, CancellationToken ct = default)
    {
        PauseWholeLineForFrontEmergency(line);
        return line switch
        {
            1 => _line1Engine == null ? "1号线前端引擎未初始化" : await _line1Engine.EmergencyDiscardCurrentAsync(skipDeviceClear, ct),
            2 => _line2Engine == null ? "2号线前端引擎未初始化" : await _line2Engine.EmergencyDiscardCurrentAsync(skipDeviceClear, ct),
            _ => $"无效线体: {line}"
        };
    }

    private void PauseWholeLineForFrontEmergency(int line)
    {
        // 前端应急可能涉及共享机械手、货叉和前天车；先同步关闭该线前后端派发门，
        // 再由引擎内部取得实际动作锁。这里只暂停，不取消动作、不释放锁。
        if (line == 1)
        {
            _line1Engine?.Pause();
            _line1RearEngine?.Pause();
            IsLine1Running = false;
        }
        else if (line == 2)
        {
            _line2Engine?.Pause();
            _line2RearEngine?.Pause();
            IsLine2Running = false;
        }
    }

    public string GetBalancingEmergencyInfo(string position)
        => _line1BalancingEngine?.GetBalancingEmergencyInfo(position) ?? "动平衡引擎未初始化";

    public Task<string> EmergencyClearBalancingPositionAsync(string position, CancellationToken ct = default)
        => _line1BalancingEngine?.EmergencyClearBalancingPositionAsync(position, IsBalancingRunning, ct)
           ?? Task.FromResult("动平衡引擎未初始化");

    public string GetGrindingEmergencyInfo(string target)
        => _grindingEngine?.GetGrindingEmergencyInfo(target) ?? "研磨引擎未初始化";

    public async Task<string> EmergencyClearGrindingAsync(string target, bool skipDeviceClear,
        bool resumeAfterClear, CancellationToken ct = default)
    {
        if (_grindingEngine == null) return "研磨引擎未初始化";

        var result = await _grindingEngine.EmergencyClearGrindingAsync(
            target, skipDeviceClear, resumeAfterClear, ct);
        // 超时或清零失败时引擎会保持暂停，主页面必须同步真实状态，不能仍显示“运行中”。
        IsGrindingRunning = _grindingEngine.IsRunning && !_grindingEngine.IsPaused;
        OnPropertyChanged(nameof(GrindingCachedCount));
        OnPropertyChanged(nameof(GrindingCacheDetail));
        return result;
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
    /// <summary>一键启动当前任务列表, 并让后续新增任务自动启动。</summary>
    public ICommand AutoStartTasksCommand { get; }

    private bool _autoStartTasks;
    public bool AutoStartTasks
    {
        get => _autoStartTasks;
        private set
        {
            if (SetField(ref _autoStartTasks, value))
                OnPropertyChanged(nameof(AutoStartTasksText));
        }
    }

    public string AutoStartTasksText => AutoStartTasks ? "停止自动启动" : "一键启动";

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
        OnPropertyChanged(nameof(FrontDispatchCachedCount));
        OnPropertyChanged(nameof(GrindingCachedCount));
        OnPropertyChanged(nameof(Line1CacheDetail));
        OnPropertyChanged(nameof(Line2CacheDetail));
        OnPropertyChanged(nameof(GrindingCacheDetail));
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
        _frontDispatchCts?.Cancel();
        _line1StatusCts = null;
        _line2StatusCts = null;
        _grindingRackStatusCts = null;
        _frontDispatchCts = null;
        _frontDispatchTask = null;
    }

    /// <summary>
    /// 停止四台研磨机的页面采集循环。只有旧循环全部退出后才允许重建生产上下文，
    /// 防止旧循环继续重连并把服务注入已经失效的研磨引擎。
    /// </summary>
    private async Task StopGrinderPollLoopsAsync()
    {
        var cts = _grinderPollCts;
        if (cts == null) return;

        var tasks = _grinderPollTasks.ToArray();
        cts.Cancel();
        try
        {
            if (tasks.Length > 0)
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException)
        {
            Console.WriteLine("[GrinderPoll] 旧研磨轮询10秒内未退出，禁止重建生产上下文");
            throw;
        }

        _grinderPollTasks.Clear();
        _grinderPollCts = null;
        cts.Dispose();
        Console.WriteLine("[GrinderPoll] 四台研磨机旧轮询已全部退出");
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
        if (FrontDispatchCachedCount > 0)
            reasons.Add($"总上料架全局待派发队列={FrontDispatchCachedCount}");
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
            bool m817 = (m816Word & (1 << RackAddr.Bit_ST019_HasPlate)) != 0;
            bool m823 = (m816Word & (1 << RackAddr.Bit_ST020_CanPick)) != 0;
            bool m825 = (m816Word & (1 << RackAddr.Bit_ST021_CanPick)) != 0;
            if (m817) reasons.Add("PLC现场M817=1(1号线动平衡下料架有板)");
            if (m823 && _line1BalancingEngine?.M818CachePresent != true)
                reasons.Add("PLC现场M823=1(ST020允许取料)但上位机M818缓存不存在");
            if (m825 && _line1BalancingEngine?.M821CachePresent != true)
                reasons.Add("PLC现场M825=1(ST021允许取料)但上位机M821缓存不存在");

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

                // 先确认持有研磨连接的旧循环退出，再创建新引擎和新连接。
                await StopGrinderPollLoopsAsync();
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
        LogDuplicateMotionDeviceMappings(machineRows);
        ManualControl.NotifyMappingsUpdated();

        // ── 装载工位坐标到引擎 ──────────────────────────────────────
        _flowEngine.LoadStationCoords(machineRows);
        Console.WriteLine("[HomeViewModel] 引擎已装载工位坐标缓存");

        // ── 创建研磨流程引擎 ─────────────────────────────────────────
        var grindingCoords = machineRows
            .Where(r => !string.IsNullOrWhiteSpace(r.StationCode))
            .ToDictionary(r => r.StationCode.Trim().ToUpperInvariant(), r => r, StringComparer.OrdinalIgnoreCase);
        _stationDict = grindingCoords; // 缓存供弹窗查询
        _grindingEngine = new GrindingFlowEngine(_craneCache, _cfg, grindingCoords, _mcCache, _exceptionReporter);
        Console.WriteLine($"[HomeViewModel] 研磨流程引擎已创建（{grindingCoords.Count} 个工位坐标）");

        // ── 前后端共享中转架互斥锁 ──
        _sharedTransferRackLock = new SemaphoreSlim(1, 1);
        // ── 机械手1只有一台, 1/2号线前端必须共用同一把锁, 防止两条线同时派机械手1 ──
        _sharedManipulatorLock = new SemaphoreSlim(1, 1);
        var sharedSafety = new SafetyFlags();

        // ── 平衡料架位置锁(4把): 保护天车和机械手同时操作同一位置, 防止碰撞 ──
        //    M818/M821保留历史命名, 实际保护2号线ST020/ST021相邻区域, 不再表示PLC有板位。
        //    持锁范围: 进入位置→放料/取料完成→离开位置后释放
        var lockM817 = new SemaphoreSlim(1, 1);
        var lockM818 = new SemaphoreSlim(1, 1);
        var lockM821 = new SemaphoreSlim(1, 1);
        var lockM720 = new SemaphoreSlim(1, 1);

        // ── 创建共享MC服务给前端引擎(避免1/2号线重复直连63) ──
        //     frontRackSvc 始终传给两个前端；即使首连失败，也只能通过_McCache重连，不能退化为各自new TCP。
        var frontRackSvc = new CenteringRackService("总上料架+中转架", _mcCache, "192.168.2.63", 9000);
        _frontRackDispatchSvc = frontRackSvc;
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
            _exceptionReporter, _sharedTransferRackLock, sharedSafety, rackSvc: frontRackSvc,
            manipulatorLock: _sharedManipulatorLock);
        Console.WriteLine("[HomeViewModel] 1号线前端流程引擎已创建" + (sharedMc63Ready ? "(共享MC63已连接)" : "(共享MC63待重连)"));

        // ── 创建1号线后端流程引擎 (中转架状态从前端DeviceStatus读取) ──
        // M817+M720 锁传给1号线后端: DoUnload长工件→M817, 短工件→M720
        _line1RearEngine = new Line1RearFlowEngine(_craneCache, _manipulatorCache, _mcCache, _cfg, grindingCoords,
            _sharedTransferRackLock, sharedSafety, _exceptionReporter, _line1Engine.DeviceStatus,
            lockM817: lockM817, lockM720: lockM720);
        Console.WriteLine("[HomeViewModel] 1号线后端流程引擎已创建");

        // ── 创建机械手2动平衡流转引擎 ──
        // 4把锁全传: M2Flow用M817或M818, M3Flow用M821+M720
        _line1BalancingEngine = new BalancingFlowEngine(_manipulatorCache, _craneCache, _mcCache, _cfg, grindingCoords,
            _exceptionReporter, lockM817, lockM818, lockM821, lockM720);
        Console.WriteLine("[HomeViewModel] 机械手2动平衡流转引擎已创建(两条线共用)");

        // ── 创建2号线流程引擎 ──────────────────────────────────────
        _sharedTransferRackLock2 = new SemaphoreSlim(1, 1);
        var sharedSafety2 = new SafetyFlags();

        _line2Engine = new Line2FrontFlowEngine(_craneCache, _manipulatorCache, _cfg, grindingCoords,
            _exceptionReporter, transferRackLock: _sharedTransferRackLock2, safety: sharedSafety2,
            rackSvc: frontRackSvc, manipulatorLock: _sharedManipulatorLock);
        Console.WriteLine("[HomeViewModel] 2号线前端流程引擎已创建(共享机械手锁)");

        // M818+M821 锁传给2号线后端: DoUnload长工件→M818, 短工件→M821
        _line2RearEngine = new Line2RearFlowEngine(_craneCache, _manipulatorCache, _mcCache, _cfg, grindingCoords,
            _sharedTransferRackLock2, sharedSafety2, _exceptionReporter, _line2Engine.DeviceStatus,
            lockM818: lockM818, lockM821: lockM821);
        Console.WriteLine("[HomeViewModel] 2号线后端流程引擎已创建");

        // 天车坐标保护统一回到主页面：后台派发确认全零后，暂停对应整线并弹窗。
        _line1Engine.OnCraneZeroPositionDetected = HandleCraneZeroPositionAlarm;
        _line1RearEngine.OnCraneZeroPositionDetected = HandleCraneZeroPositionAlarm;
        _line2Engine.OnCraneZeroPositionDetected = HandleCraneZeroPositionAlarm;
        _line2RearEngine.OnCraneZeroPositionDetected = HandleCraneZeroPositionAlarm;
        _grindingEngine!.OnCraneZeroPositionDetected = HandleCraneZeroPositionAlarm;
        _line1RearEngine.OnRearCraneSafetyAlarm = message => HandleLineSafetyAlarm(1, "后天车安全异常", message);
        _line2RearEngine.OnRearCraneSafetyAlarm = message => HandleLineSafetyAlarm(2, "后天车安全异常", message);
        _line1RearEngine.OnRearCraneX11FinalSafetyAlarm = message => HandleLineSafetyAlarm(1, "后天车X11取料失败", message, recordLegacyEvent: false);
        _line2RearEngine.OnRearCraneX11FinalSafetyAlarm = message => HandleLineSafetyAlarm(2, "后天车X11取料失败", message, recordLegacyEvent: false);
        _line1RearEngine.OnRearCraneWarning = message => HandleRearCraneWarning(1, message);
        _line2RearEngine.OnRearCraneWarning = message => HandleRearCraneWarning(2, message);
        _line1Engine.OnSafetyAlarm = message => HandleLineSafetyAlarm(1, "前端流程异常", message);
        _line2Engine.OnSafetyAlarm = message => HandleLineSafetyAlarm(2, "前端流程异常", message);
        // 机械手1由两条线共享。若货叉放行后的预定位停在未知中间位置，必须在释放共享锁前同步关闭两线派发门。
        _line1Engine.OnSharedManipulatorSafetyAlarm = HandleSharedManipulatorSafetyAlarm;
        _line2Engine.OnSharedManipulatorSafetyAlarm = HandleSharedManipulatorSafetyAlarm;
        _line1BalancingEngine.OnSafetyAlarm = message => HandleStandaloneSafetyAlarm("动平衡", message);
        _grindingEngine.OnSafetyAlarm = message => HandleStandaloneSafetyAlarm("研磨", message);

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

        // ── 启动状态同步（引擎缓存→UI卡片，不另建连接，约1.5s刷新）───
        _line1StatusCts = new CancellationTokenSource();
        _ = SyncLine1StatusToCardsAsync(_line1StatusCts.Token);
        _line2StatusCts = new CancellationTokenSource();
        _ = SyncLine2StatusToCardsAsync(_line2StatusCts.Token);
        _grindingRackStatusCts = new CancellationTokenSource();
        _ = SyncGrindingRackStatusToCardsAsync(_grindingRackStatusCts.Token);
        StartFrontDispatchLoop();

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
        EnsureNewCard("ST103", "一号双头镗");       // → ST103 双头镗:192.168.2.66 (引擎托管)
        EnsureNewCard("ST501", "一号打号机");       // → ST501 文件:D:\1 (引擎托管)
        EnsureNewCard("ST901", "1号线天车前");      // → ST901 Modbus:192.168.2.81 (引擎托管)
        EnsureNewCard("ST902", "1号线天车后");      // → 2号天车 Modbus:192.168.2.82 (引擎托管)
        EnsureNewCard("ST105", "1号线中转架1");     // M811 192.168.2.63
        EnsureNewCard("ST101", "1号线中转架2");     // M812 192.168.2.63
        EnsureNewCard("ST106", "1号线中转架3");     // M813 192.168.2.63
        // 2号线前端/中转
        EnsureNewCard("ST712", "货叉2");            // → ST712 MC:192.168.2.89
        EnsureNewCard("ST104", "2号线天车前");      // → 3号天车 Modbus:192.168.2.83
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
        EnsureNewCard("ST710", "研磨机下料架");     // → ST710 MC64: M720允许放料/M721放料完成
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

        // ── 一次性探测：仅使用共享MC缓存的料架PLC。
        // 货叉、双头镗、斜床全部由各自引擎连接并同步卡片，页面不再建第二条连接。
        _ = ProbeMcDevicesAsync();

        
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
        // 跳过引擎已管理的设备以及非ModbusTCP协议设备。
        // 后端FANUC斜床不做页面直连探测, 只显示后端引擎Worker缓存的状态。
        var engineManagedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ST007", "ST711", "ST103", "ST107", "ST901", "ST002", "ST501", "ST605", "ST402",
            // 2号线后端下料架使用M818/M819/M823/M824、M820/M821/M825/M826组合握手,
            // 不能再由单个M818/M821直连探测解释成"有板/无板"。
            "ST020", "ST021",
            // 1号线Modbus斜床也由后端引擎持有唯一连接，页面只读DeviceStatus。
            "ST108", "ST109", "ST110", "ST111",
            // 后端FANUC斜床由LineRear引擎的FanucSkewBedService独立Worker读取缓存。
            // 页面不能再直接FOCAS探测, 避免Task.Run阻塞线程池并覆盖引擎状态。
            "ST112", "ST606", "ST607", "ST608", "ST609", "ST610"
        };
        _ = Task.Run(async () =>
        {
            var tasks = newCards.Values.Where(c => c.HasIp && !engineManagedCodes.Contains(c.StationCode)).Select(c => SafeStartStationAsync(c)).ToList();
            if (tasks.Count > 0) await Task.WhenAll(tasks);
            Console.WriteLine($"[HomeViewModel] {tasks.Count} 个站卡后台连接完成（已跳过引擎托管设备）");
        });
        StartGrinderConnections(newCards);
        Console.WriteLine("[HomeViewModel] 研磨机GrinderPoll后台轮询已启动");

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
    /// 有IP则用PlcGrinderService连，按研磨配置周期读状态并更新对应站卡显示。
    /// </summary>
    private void StartGrinderConnections(Dictionary<string, StationCardViewModel> cards)
    {
        if (_grinderPollCts != null)
            throw new InvalidOperationException("研磨机轮询已经启动，禁止重复创建");

        _grinderPollCts = new CancellationTokenSource();
        var pollToken = _grinderPollCts.Token;
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
            int pollMs = Math.Max(500, _cfg.Grinding.PollIntervalMs);
            Console.WriteLine($"[GrinderPoll] {name} ({code}) 启动轮询 Type={gtype} IP={ip} poll={pollMs}ms");
            _grinderPollTasks.Add(
                GrinderPollLoopAsync(code, name, gtype, ip, card, grinderCard, _grindingEngine!, pollMs, pollToken));
        }
    }

    /// <summary>
    /// 只做启动诊断，不改变数据库匹配结果。重复IP可能让两个设备编号创建到同一PLC的两条连接，
    /// 因此在现场启动日志中明确列出，避免映射错误被误认为通信抖动。
    /// </summary>
    private static void LogDuplicateMotionDeviceMappings(IEnumerable<MachineManagementRowVm> machineRows)
    {
        var motionRows = machineRows
            .Where(r => r.TypeName?.Trim() is "天车" or "机械手")
            .Where(r => !string.IsNullOrWhiteSpace(r.Ip))
            .ToList();

        foreach (var group in motionRows.GroupBy(r => r.Ip.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            string devices = string.Join(", ", group.Select(r => $"{r.StationCode}/{r.Name}/{r.TypeName}"));
            Console.WriteLine($"[HomeViewModel] ⚠ 运动设备IP重复 IP={group.Key}: {devices}");
        }

        foreach (var group in motionRows
                     .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                     .GroupBy(r => r.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            string records = string.Join(", ", group.Select(r => $"{r.StationCode}/{r.Ip}/{r.TypeName}"));
            Console.WriteLine($"[HomeViewModel] ⚠ 运动设备名称重复 Name={group.Key}: {records}");
        }
    }

    /// <summary>研磨机轮询循环：连接 → 快速读状态 → 更新站卡 → 断开重连。日志按变化/周期节流。</summary>
    private static async Task GrinderPollLoopAsync(string code, string name,
        PlcGrinderService.GrinderType gtype, string ip, StationCardViewModel stationCard,
        GrinderCardViewModel grinderCard, GrindingFlowEngine grindingEngine, int pollDelayMs,
        CancellationToken ct)
    {
        PlcGrinderService? svc = null;
        int retryDelay = 5000;
        int cycleCount = 0;
        string? lastLogKey = null;
        int logEveryCycles = Math.Max(1, 5000 / Math.Max(1, pollDelayMs));

        Console.WriteLine($"[GrinderPoll] [{name}] ========== 轮询线程启动 IP={ip} ==========");

        try
        {
            while (!ct.IsCancellationRequested)
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
                        await Task.Delay(retryDelay, ct);
                        retryDelay = Math.Min(retryDelay * 2, 30000);
                        continue;
                    }

                    Console.WriteLine($"[GrinderPoll] [{name}] {(svc == null ? "首次连接" : "重连")} IP={ip}...");
                    var oldSvc = svc;
                    var newSvc = new PlcGrinderService(name, ip, gtype);
                    try
                    {
                        using var connectTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct, connectTimeoutCts.Token);
                        await newSvc.ConnectAsync(connectCts.Token);
                    }
                    catch
                    {
                        // 连接尚未注入引擎，由本轮创建者负责释放。
                        newSvc.Dispose();
                        throw;
                    }

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
                        await Task.Delay(retryDelay, ct);
                        retryDelay = Math.Min(retryDelay * 2, 30000);
                        continue;
                    }

                    oldSvc?.Dispose();
                    svc = newSvc;
                    Console.WriteLine($"[GrinderPoll] [{name}] ✔ 连接成功 IP={ip} Type={gtype}");
                    retryDelay = 5000;
                    stationCard.ConnectedBrush = System.Windows.Media.Brushes.LimeGreen;
                    // 注入共享服务到研磨机卡片 + 研磨流程引擎，避免各自另建连接导致双连接冲突
                    grinderCard.SetSharedService(svc);
                }

                // 读全部状态
                using var readTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct, readTimeoutCts.Token);
                if (gtype == PlcGrinderService.GrinderType.TypeA)
                {
                    var snapshot = await svc.ReadTypeAStatusSnapshotAsync(readCts.Token);
                    bool fault   = snapshot.Alarm;
                    bool stone1  = snapshot.GrindStone1Alarm;
                    bool stone2  = snapshot.GrindStone2Alarm;
                    bool reqData = snapshot.ReqData;
                    bool reqLoad = snapshot.ReqLoad;
                    bool clamped = snapshot.Clamped;
                    bool reqUnld = snapshot.ReqUnload;
                    bool unclamp = snapshot.Unclamp;
                    bool busy    = snapshot.Busy;
                    bool door    = snapshot.Door;

                    int stoneNo = stone1 ? 1 : 2;
                    string state = BuildGrinderOverviewState(fault, false, stone1 || stone2, stoneNo,
                        reqData, reqLoad, clamped, reqUnld, unclamp, busy);
                    stationCard.ConnectedBrush = fault || stone1 || stone2 ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.LimeGreen;
                    stationCard.Status1 = state;
                    stationCard.Status1Brush = GetSignalStateBrush(state);
                    stationCard.Status2 = $"DI=0x{snapshot.RawDI:X4} b12={To01(reqUnld)} 数据={To01(reqData)} 上料={To01(reqLoad)} 锁紧={To01(clamped)} 下料={To01(reqUnld)} 松开={To01(unclamp)} 加工={To01(busy)} 门={(door ? "开" : "关")} 磨石1={To01(stone1)} 磨石2={To01(stone2)}";

                    // 同步更新研磨机卡片（共享同一快照）
                    grinderCard.UpdateTypeA(snapshot);

                    string logKey = $"A:{snapshot.RawDI:X4}";
                    if (ShouldLogGrinderPoll(cycleCount, logEveryCycles, logKey, ref lastLogKey))
                    {
                        Console.WriteLine($"[GrinderPoll] [{name}] #{cycleCount} TypeA快照 {snapshot.ToSignalText()} " +
                            $"报警={fault} 请求数据={reqData} 请求上料={reqLoad} 锁紧={clamped} 请求下料={reqUnld} 松开={unclamp} 加工={busy} 门开={door} 磨石1={stone1} 磨石2={stone2}");
                    }
                }
                else
                {
                    var snapshot = await svc.ReadTypeBStatusSnapshotAsync(readCts.Token);
                    int status   = snapshot.MachineStatus;
                    bool reqData = snapshot.ReqData;
                    bool reqLoad = snapshot.ReqLoad;
                    bool clamped = snapshot.Clamped;
                    bool reqUnld = snapshot.ReqUnload;
                    bool unclamp = snapshot.Unclamp;
                    bool busy    = snapshot.Busy;
                    bool door    = snapshot.Door;

                    string state = BuildGrinderOverviewState(false, status == 2, false, 0,
                        reqData, reqLoad, clamped, reqUnld, unclamp, busy, status);
                    stationCard.ConnectedBrush = status == 2 ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.LimeGreen;
                    stationCard.Status1 = state;
                    stationCard.Status1Brush = GetSignalStateBrush(state);
                    stationCard.Status2 = $"R7304={snapshot.RequestUnload} R7308={status} 数据={To01(reqData)} 上料={To01(reqLoad)} 锁紧={To01(clamped)} 下料={To01(reqUnld)} 松开={To01(unclamp)} 加工={To01(busy)} 门={(door ? "开" : "关")}";

                    // 同步更新研磨机卡片
                    grinderCard.UpdateTypeB(status, reqData, reqLoad, clamped, reqUnld, unclamp, busy, door);

                    string logKey = $"B:{snapshot.ToSignalText()}";
                    if (ShouldLogGrinderPoll(cycleCount, logEveryCycles, logKey, ref lastLogKey))
                    {
                        Console.WriteLine($"[GrinderPoll] [{name}] #{cycleCount} TypeB快照 {snapshot.ToSignalText()} " +
                            $"请求下料={reqUnld} R7304地址=14609");
                    }
                }

                    await Task.Delay(pollDelayMs, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GrinderPoll] [{name}] #{cycleCount} ✘ 异常：{ex.GetType().Name} — {ex.Message}");
                    if (svc != null && grindingEngine.CanReplaceGrinderService(code, out _))
                    {
                        try { await svc.DisconnectAsync(); } catch { }
                    }
                    stationCard.Status1 = "连接失败";
                    stationCard.Status2 = "等待重连...";
                    stationCard.ConnectedBrush = System.Windows.Media.Brushes.Gray;
                    stationCard.Status1Brush = System.Windows.Media.Brushes.Gray;
                    grinderCard.SetDisconnected();
                    Console.WriteLine($"[GrinderPoll] [{name}] 退避 {retryDelay / 1000}s 后重试...");
                    await Task.Delay(retryDelay, ct);
                    retryDelay = Math.Min(retryDelay * 2, 30000);
                }
            }
        }
        finally
        {
            if (svc != null)
            {
                if (!grindingEngine.CanReplaceGrinderService(code, out var holdReason))
                    throw new InvalidOperationException($"[GrinderPoll] [{name}] 退出时服务仍被动作占用: {holdReason}");

                try { await svc.DisconnectAsync(); } catch (Exception ex) { Console.WriteLine($"[GrinderPoll] [{name}] 退出断开异常: {ex.Message}"); }
                svc.Dispose();
            }
            grinderCard.SetDisconnected();
            Console.WriteLine($"[GrinderPoll] [{name}] 轮询已退出");
        }
    }

    private static bool ShouldLogGrinderPoll(int cycleCount, int logEveryCycles, string logKey, ref string? lastLogKey)
    {
        bool changed = !string.Equals(lastLogKey, logKey, StringComparison.Ordinal);
        bool periodic = cycleCount == 1 || cycleCount % logEveryCycles == 1;
        if (!changed && !periodic) return false;
        lastLogKey = logKey;
        return true;
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
                if (IsTaskDisplayTerminal(step, state))
                {
                    if (TaskRows.Remove(row))
                        Console.WriteLine($"[HomeViewModel] 自动清除任务显示：版号={row.PlateNo} 序号={row.Sequence} 终点={step}/{state}");
                }
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

    private static bool IsTaskDisplayTerminal(string step, string state)
    {
        return string.Equals(step, "动平衡加工中 ST008/M710", StringComparison.Ordinal)
            || string.Equals(step, "已完成", StringComparison.Ordinal)
            || string.Equals(state, "已完成", StringComparison.Ordinal);
    }

    private void StartFrontDispatchLoop()
    {
        if (_frontDispatchTask is { IsCompleted: false })
            return;

        _frontDispatchCts?.Cancel();
        _frontDispatchCts = new CancellationTokenSource();
        _frontDispatchTask = Task.Run(() => FrontDispatchLoopAsync(_frontDispatchCts.Token));
        Console.WriteLine("[FrontDispatch] 总上料架全局派发循环已启动");
    }

    private void EnqueueFrontDispatch(TaskRowViewModel row, WorkpieceCache wp)
    {
        lock (_frontDispatchLock)
        {
            if (_frontDispatchQueue.Any(x => ReferenceEquals(x.Row, row)))
            {
                Console.WriteLine($"[FrontDispatch] {wp.IdentityText} 已在全局队列中, 跳过重复入队");
                wp.ReportStage("等待总上料架派发");
                return;
            }

            _frontDispatchQueue.Enqueue(new FrontDispatchItem(row, wp));
        }

        wp.ReportStage("等待总上料架派发");
        OnPropertyChanged(nameof(FrontDispatchCachedCount));
        Console.WriteLine($"[FrontDispatch] 📥 入全局FIFO {wp.IdentityText} d={wp.Diameter} L={wp.Length} 队列={FrontDispatchCachedCount}");
    }

    private bool TryPeekFrontDispatch(out FrontDispatchItem? item)
    {
        lock (_frontDispatchLock)
            return _frontDispatchQueue.TryPeek(out item);
    }

    private bool TryDequeueFrontDispatch(FrontDispatchItem expected, out FrontDispatchItem? item)
    {
        lock (_frontDispatchLock)
        {
            if (!_frontDispatchQueue.TryPeek(out var head) || !ReferenceEquals(head, expected))
            {
                item = null;
                return false;
            }

            item = _frontDispatchQueue.Dequeue();
            return true;
        }
    }

    private bool RemoveFrontDispatch(TaskRowViewModel row)
    {
        lock (_frontDispatchLock)
        {
            if (_frontDispatchQueue.Count == 0) return false;

            bool removed = false;
            var kept = new Queue<FrontDispatchItem>(_frontDispatchQueue.Count);
            while (_frontDispatchQueue.Count > 0)
            {
                var item = _frontDispatchQueue.Dequeue();
                if (ReferenceEquals(item.Row, row))
                {
                    removed = true;
                    continue;
                }

                kept.Enqueue(item);
            }

            while (kept.Count > 0)
                _frontDispatchQueue.Enqueue(kept.Dequeue());

            return removed;
        }
    }

    private void RequeueFrontDispatchHead(FrontDispatchItem item)
    {
        lock (_frontDispatchLock)
        {
            var rebuilt = new Queue<FrontDispatchItem>(_frontDispatchQueue.Count + 1);
            rebuilt.Enqueue(item);
            while (_frontDispatchQueue.Count > 0)
                rebuilt.Enqueue(_frontDispatchQueue.Dequeue());
            while (rebuilt.Count > 0)
                _frontDispatchQueue.Enqueue(rebuilt.Dequeue());
        }

        OnPropertyChanged(nameof(FrontDispatchCachedCount));
        Console.WriteLine($"[FrontDispatch] ↩ 派发失败, 已放回全局队头 {item.Workpiece.IdentityText} 队列={FrontDispatchCachedCount}");
    }

    private async Task<CenteringRackStatus?> ReadFrontRackStatusForDispatchAsync(CancellationToken ct)
    {
        var svc = _frontRackDispatchSvc;
        if (svc == null)
            return null;

        using var timeoutCts = new CancellationTokenSource(3000);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        return await svc.ReadAllStatusAsync(linked.Token);
    }

    private static Task DelayFrontDispatchPollAsync(CancellationToken ct)
        => Task.Delay(FrontDispatchPollInterval, ct);

    private async Task FrontDispatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _frontDispatchLoopCycle++;

                bool hasPendingItem = TryPeekFrontDispatch(out var item) && item != null;

                // 派发上一块后必须亲眼看到M800从1复位为0，才能把下一次M800=1认作新物理板。
                // 这个复位观察不能依赖全局FIFO非空：若上一块取走时FIFO刚好为空，跳过读取会漏掉
                // M800=0，后续新板已经让M800重新变1时，调度器会永久误认为仍在等待上一块复位。
                if (!hasPendingItem && !_frontDispatchWaitingM800Clear)
                {
                    await DelayFrontDispatchPollAsync(ct);
                    continue;
                }

                // 有待派发任务，或仍在等待上一块M800复位时，都必须读取现场快照。
                // 空闲且复位门已解除时不读取，避免全局调度器无意义地持续占用MC63通信。
                var status = await ReadFrontRackStatusForDispatchAsync(ct);
                if (status == null)
                {
                    if (_frontDispatchLoopCycle % 10 == 1)
                        Console.WriteLine(
                            $"[FrontDispatch] ⚠ 总上料架服务未初始化, 等待LoadAsync完成; " +
                            $"复位门={(_frontDispatchWaitingM800Clear ? "等待上一块M800=0" : "已就绪")} " +
                            $"全局FIFO={FrontDispatchCachedCount}");
                    item?.Workpiece.ReportStage("等待总上料架通信/服务初始化", "等待中");
                    await DelayFrontDispatchPollAsync(ct);
                    continue;
                }

                if (_frontDispatchWaitingM800Clear)
                {
                    if (!status.RequestPickup)
                    {
                        _frontDispatchWaitingM800Clear = false;
                        string nextTask = item?.Workpiece.IdentityText ?? "无(FIFO为空)";
                        Console.WriteLine(
                            $"[FrontDispatch] ✓ 已观察到上一块M800复位为0, 新物理板派发门已解除; " +
                            $"全局FIFO={FrontDispatchCachedCount} 下一任务={nextTask} D100={status.PlateLength}mm");
                        item?.Workpiece.ReportStage("上一块M800已复位，等待新板M800=1", "等待中");
                    }
                    else if (_frontDispatchLoopCycle % 10 == 1)
                    {
                        string nextTask = item?.Workpiece.IdentityText ?? "无(FIFO为空)";
                        Console.WriteLine(
                            $"[FrontDispatch] ⏳ 等待上一块M800复位: 当前M800=1 D100={status.PlateLength}mm " +
                            $"全局FIFO={FrontDispatchCachedCount} 下一任务={nextTask}; " +
                            "即使FIFO为空也会持续读取, 禁止把同一块物理板重复派发");
                        item?.Workpiece.ReportStage("等待上一块M800复位", "等待中");
                    }

                    await DelayFrontDispatchPollAsync(ct);
                    continue;
                }

                // 正常情况下无任务已在方法开头返回；保留兜底，防止UI删除队列与本轮快照并发交错。
                if (!hasPendingItem || item == null)
                {
                    await DelayFrontDispatchPollAsync(ct);
                    continue;
                }

                if (!item.Row.IsRunning)
                {
                    if (_frontDispatchLoopCycle % 10 == 1)
                        Console.WriteLine($"[FrontDispatch] 队头{item.Workpiece.IdentityText} 当前任务行未启动/已暂停, 保持FIFO等待恢复或删除");
                    item.Workpiece.ReportStage("任务行未启动/已暂停，等待恢复或删除", "等待中");
                    await DelayFrontDispatchPollAsync(ct);
                    continue;
                }

                if (!status.RequestPickup)
                {
                    if (_frontDispatchLoopCycle % 20 == 1)
                        Console.WriteLine($"[FrontDispatch] 等M800=1 当前队头={item.Workpiece.IdentityText} 全局队列={FrontDispatchCachedCount}");
                    item.Workpiece.ReportStage("等待总上料架M800=1（物理板到位）", "等待中");
                    await DelayFrontDispatchPollAsync(ct);
                    continue;
                }

                int line1FrontCount = _line1Engine?.CachedCount ?? 0;
                int line2FrontCount = _line2Engine?.CachedCount ?? 0;
                if (line1FrontCount > 0 || line2FrontCount > 0)
                {
                    if (_frontDispatchLoopCycle % 10 == 1)
                        Console.WriteLine($"[FrontDispatch] 等线路前端缓存消化: 1号={line1FrontCount} 2号={line2FrontCount}");
                    item.Workpiece.ReportStage($"物理板已到位，等待线路前端缓存消化：1号={line1FrontCount}，2号={line2FrontCount}", "等待中");
                    await DelayFrontDispatchPollAsync(ct);
                    continue;
                }

                int line = SelectRouteLineBySkewCapacity(item.Row, out string routeReason);
                if (line == 0)
                {
                    if (_frontDispatchLoopCycle % 10 == 1)
                        Console.WriteLine($"[FrontDispatch] 队头暂不能派发 {item.Workpiece.IdentityText}: {routeReason}");
                    item.Workpiece.ReportStage(routeReason, "等待中");
                    await DelayFrontDispatchPollAsync(ct);
                    continue;
                }

                // 选线和真正写入线路缓存之间仍可能跨过一个调度间隙。
                // 因此出全局FIFO前再读一次只读准入快照；若现场状态变了，任务继续留在队头，不盲目塞入线路缓存。
                var selectedReadiness = GetFrontDispatchReadiness(line);
                if (!selectedReadiness.CanAccept)
                {
                    string waitReason = $"物理板已到位，等待{line}号线可接板：{selectedReadiness.RejectReason}";
                    if (_frontDispatchLoopCycle % 10 == 1)
                        Console.WriteLine($"[FrontDispatch] 二次准入失败 {item.Workpiece.IdentityText}: {waitReason}");
                    item.Workpiece.ReportStage(waitReason, "等待中");
                    await DelayFrontDispatchPollAsync(ct);
                    continue;
                }

                if (!TryDequeueFrontDispatch(item, out var dequeued) || dequeued == null)
                    continue;

                try
                {
                    await RunOnUiAsync(() =>
                    {
                        dequeued.Row.AssignedLine = line;
                        OnPropertyChanged(nameof(FrontDispatchCachedCount));
                        OnPropertyChanged(nameof(Line1CachedCount));
                        OnPropertyChanged(nameof(Line2CachedCount));
                        return true;
                    });

                    if (line == 1)
                    {
                        if (_line1Engine == null) throw new InvalidOperationException("1号线前端引擎为空");
                        _line1Engine.EnqueueWorkpiece(dequeued.Workpiece);
                    }
                    else
                    {
                        if (_line2Engine == null) throw new InvalidOperationException("2号线前端引擎为空");
                        _line2Engine.EnqueueWorkpiece(dequeued.Workpiece);
                    }
                }
                catch (Exception ex)
                {
                    dequeued.Row.AssignedLine = 0;
                    RequeueFrontDispatchHead(dequeued);
                    Console.WriteLine($"[FrontDispatch] ⚠ 写入{line}号线前端缓存失败, 已回队头: {ex.GetType().Name} - {ex.Message}");
                    dequeued.Workpiece.ReportStage($"写入{line}号线前端缓存失败，已回全局队头：{ex.Message}", "等待中");
                    await DelayFrontDispatchPollAsync(ct);
                    continue;
                }

                await RunOnUiAsync(() =>
                {
                    OnPropertyChanged(nameof(Line1CachedCount));
                    OnPropertyChanged(nameof(Line2CachedCount));
                    OnPropertyChanged(nameof(Line1CacheDetail));
                    OnPropertyChanged(nameof(Line2CacheDetail));
                    return true;
                });

                dequeued.Workpiece.ReportStage($"已派发{line}号线前端缓存");
                _lastFrontDispatchLine = line;
                _frontDispatchWaitingM800Clear = true;
                Console.WriteLine(
                    $"[FrontDispatch] 🚀 M800=1 D100={status.PlateLength}mm 队头{dequeued.Workpiece.IdentityText} " +
                    $"D={dequeued.Workpiece.Diameter} L={dequeued.Workpiece.Length} → {line}号线; {routeReason}; " +
                    $"剩余={FrontDispatchCachedCount}; 已进入M800复位门, 必须观察到M800=0后才允许下一块派发");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_frontDispatchLoopCycle % 10 == 1)
                    Console.WriteLine($"[FrontDispatch] ⚠ 派发循环异常: {ex.GetType().Name} - {ex.Message}");
                await DelayFrontDispatchPollAsync(ct);
            }
        }

        Console.WriteLine("[FrontDispatch] 总上料架全局派发循环已停止");
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
            InnerTaper = taskRow.InnerTaper,
            CornerSize = taskRow.CornerSize,
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
        if (length <= 0)
        {
            message = "版长必须大于0";
            return false;
        }

        if (_cfg.SkewBed.AnyBedCanProcessLine(line, length))
        {
            message = string.Empty;
            return true;
        }

        message = $"{line}号线没有可加工 {length}mm 版长的斜床, 请检查斜床最大加工长度配置";
        return false;
    }

    private FrontDispatchReadinessSnapshot GetFrontDispatchReadiness(int line)
    {
        return line switch
        {
            1 when !_isLine1Running => new FrontDispatchReadinessSnapshot(1, false, "1号线未启动或已暂停", 999, DateTime.UtcNow),
            2 when !_isLine2Running => new FrontDispatchReadinessSnapshot(2, false, "2号线未启动或已暂停", 999, DateTime.UtcNow),
            1 when _line1Engine != null => _line1Engine.GetFrontDispatchReadiness(),
            2 when _line2Engine != null => _line2Engine.GetFrontDispatchReadiness(),
            1 or 2 => new FrontDispatchReadinessSnapshot(line, false, "前端引擎未初始化", 999, DateTime.UtcNow),
            _ => new FrontDispatchReadinessSnapshot(line, false, "无效线号", 999, DateTime.UtcNow)
        };
    }

    private int SelectRouteLineBySkewCapacity(TaskRowViewModel taskRow, out string routeReason)
    {
        double length = taskRow.Length;
        int largeDiameterLine1OnlyMm = _cfg.SkewBed.LargeDiameterLine1OnlyMm;
        bool line1CanProcess = _cfg.SkewBed.AnyBedCanProcessLine(1, length);
        bool line2CanProcess = _cfg.SkewBed.AnyBedCanProcessLine(2, length);
        var line1Ready = GetFrontDispatchReadiness(1);
        var line2Ready = GetFrontDispatchReadiness(2);

        if (largeDiameterLine1OnlyMm > 0 && taskRow.Diameter >= largeDiameterLine1OnlyMm)
        {
            if (!line1CanProcess)
            {
                routeReason = $"直径{taskRow.Diameter}mm>={largeDiameterLine1OnlyMm}mm需走1号线, 但1号线无可加工{length}mm的斜床";
                return 0;
            }

            if (!line1Ready.CanAccept)
            {
                routeReason = $"大直径必须走1号线，等待1号线可接板：{line1Ready.RejectReason}";
                return 0;
            }

            routeReason = $"直径{taskRow.Diameter}mm>={largeDiameterLine1OnlyMm}mm, 强制1号线防止穿越2号线货叉区域大板干涉；1号线可接板(压力={line1Ready.Pressure})";
            return 1;
        }

        if (!line1CanProcess && !line2CanProcess)
        {
            routeReason = $"版长{length}mm无可加工斜床";
            return 0;
        }

        if (line1CanProcess && !line2CanProcess)
        {
            if (!line1Ready.CanAccept)
            {
                routeReason = $"版长{length}mm仅1号线有斜床可加工，但1号线暂不可接板：{line1Ready.RejectReason}";
                return 0;
            }

            routeReason = $"版长{length}mm仅1号线有斜床可加工；1号线可接板(压力={line1Ready.Pressure})";
            return 1;
        }

        if (!line1CanProcess && line2CanProcess)
        {
            if (!line2Ready.CanAccept)
            {
                routeReason = $"版长{length}mm仅2号线有斜床可加工，但2号线暂不可接板：{line2Ready.RejectReason}";
                return 0;
            }

            routeReason = $"版长{length}mm仅2号线有斜床可加工；2号线可接板(压力={line2Ready.Pressure})";
            return 2;
        }

        if (!line1Ready.CanAccept && !line2Ready.CanAccept)
        {
            routeReason = $"物理板已到位，等待可接线路：1号线={line1Ready.RejectReason}；2号线={line2Ready.RejectReason}";
            return 0;
        }

        if (!line1Ready.CanAccept)
        {
            routeReason = $"两线斜床均可加工，1号线暂不可接板：{line1Ready.RejectReason}；选择2号线(压力={line2Ready.Pressure})";
            return 2;
        }

        if (!line2Ready.CanAccept)
        {
            routeReason = $"两线斜床均可加工，2号线暂不可接板：{line2Ready.RejectReason}；选择1号线(压力={line1Ready.Pressure})";
            return 1;
        }

        int selected;
        if (line1Ready.Pressure == line2Ready.Pressure)
        {
            selected = _lastFrontDispatchLine == 1 ? 2 : 1;
            routeReason = $"两线斜床均可加工且都可接板，压力相同(1号={line1Ready.Pressure},2号={line2Ready.Pressure})，按上次成功分线={(_lastFrontDispatchLine == 0 ? "无" : $"{_lastFrontDispatchLine}号线")}交替选择{selected}号线";
            return selected;
        }

        selected = line1Ready.Pressure < line2Ready.Pressure ? 1 : 2;
        routeReason = $"两线斜床均可加工且都可接板，按前端压力少优先(1号={line1Ready.Pressure},2号={line2Ready.Pressure})";
        return selected;
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

        var lines = GetErpTaskLines(secondRead);
        if (lines.Count == 0)
        {
            await SetErpTaskImportStatusAsync($"ERP监听：监听中 {path}");
            return;
        }

        // TaskRows是UI绑定集合, 必须回到WPF UI线程写入。
        // 这里只调用AddTask, 不启动任务, 后续仍由人工点击“启动”进入原业务流程。
        var importResult = await RunOnUiAsync(() => ImportErpTaskLines(lines));

        await RewriteErpTaskFileAsync(path, importResult.RetainedLines, ct);
        if (importResult.InvalidLines.Count == 0)
        {
            _lastErpInvalidBackupSignature = string.Empty;
        }
        else if (!string.Equals(_lastErpInvalidBackupSignature, importResult.InvalidMessage, StringComparison.Ordinal))
        {
            await BackupBadErpTaskAsync(string.Join(Environment.NewLine, importResult.InvalidLines), importResult.InvalidMessage, ct);
            _lastErpInvalidBackupSignature = importResult.InvalidMessage;
        }

        await SetErpTaskImportStatusAsync(
            $"ERP监听：导入{importResult.ImportedCount}行, 保留重复{importResult.DuplicateCount}行, 保留异常{importResult.InvalidLines.Count}行");
        Console.WriteLine($"[ERP导入] ✓ 批量处理完成: 导入={importResult.ImportedCount} 重复保留={importResult.DuplicateCount} 异常保留={importResult.InvalidLines.Count}");
    }

    private static List<string> GetErpTaskLines(string content)
    {
        return content.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private ErpBatchImportResult ImportErpTaskLines(IReadOnlyList<string> lines)
    {
        var retainedLines = new List<string>();
        var invalidLines = new List<string>();
        var invalidMessages = new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in TaskRows)
            seenKeys.Add(BuildErpTaskKey(task.PlateNo, task.Sequence));

        int imported = 0;
        int duplicates = 0;
        foreach (string line in lines)
        {
            if (!ErpTaskImportParser.TryParse(line, out var row, out string parseError) || row == null)
            {
                retainedLines.Add(line);
                invalidLines.Add(line);
                invalidMessages.Add($"[{line}] {parseError}");
                Console.WriteLine($"[ERP导入] ⚠ 解析失败, 保留原行: {parseError}; 原始='{line}'");
                continue;
            }

            string key = BuildErpTaskKey(row.PlateNo, row.Sequence);
            if (!seenKeys.Add(key))
            {
                duplicates++;
                retainedLines.Add(line);
                Console.WriteLine($"[ERP导入] ⚠ 任务重复, 保留原行: 版号={row.PlateNo} 序号={row.Sequence}");
                continue;
            }

            AddTask(row);
            imported++;
            Console.WriteLine($"[ERP导入] ✓ 已导入: 版号={row.PlateNo} 序号={row.Sequence}; 原始='{line}'");
        }

        return new ErpBatchImportResult(imported, duplicates, retainedLines, invalidLines,
            invalidMessages.Count == 0 ? string.Empty : string.Join(Environment.NewLine, invalidMessages));
    }

    private static string BuildErpTaskKey(string plateNo, string sequence)
        => $"{plateNo.Trim()}\u001F{sequence.Trim()}";

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

    private static async Task RewriteErpTaskFileAsync(string path, IReadOnlyList<string> retainedLines, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        if (retainedLines.Count > 0)
        {
            string text = string.Join(Environment.NewLine, retainedLines) + Environment.NewLine;
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(text);
            await fs.WriteAsync(bytes, ct);
        }

        await fs.FlushAsync(ct);
        Console.WriteLine(retainedLines.Count == 0
            ? $"[ERP导入] 已清空ERP任务文件: {path}"
            : $"[ERP导入] 已回写ERP任务文件: {path}, 保留{retainedLines.Count}行");
    }

    private sealed record ErpBatchImportResult(
        int ImportedCount,
        int DuplicateCount,
        List<string> RetainedLines,
        List<string> InvalidLines,
        string InvalidMessage);

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

    private async Task AutoStartTasksAsync()
    {
        if (AutoStartTasks)
        {
            AutoStartTasks = false;
            Console.WriteLine("[HomeViewModel] ⏹ 已停止任务自动启动。已入队/已派发任务不受影响。");
            await Task.CompletedTask;
            return;
        }

        AutoStartTasks = true;
        int started = 0;
        foreach (var row in TaskRows.ToList())
        {
            if (row.IsRunning) continue;
            row.RequestStart();
            if (row.IsRunning) started++;
        }

        Console.WriteLine($"[HomeViewModel] ▶ 一键启动完成: 本次启动{started}个任务, 后续新增任务将自动启动");
        await Task.CompletedTask;
    }

    public void AddTask(TaskRowViewModel row)
    {
        row.OnDeleteRequested = ClearTaskRow;

        // 设置启动回调：用户点任务行的「启动」→ 分配工件到对应线路
        row.OnStartRequested = taskRow => StartTaskRow(taskRow);

        TaskRows.Add(row);
        Console.WriteLine($"[HomeViewModel] 新增任务：版号={row.PlateNo} 序号={row.Sequence} 版长={row.Length}mm");
        if (AutoStartTasks)
            row.RequestStart();
    }

    private void StartTaskRow(TaskRowViewModel taskRow)
    {
        if (taskRow.AssignedLine > 0)
        {
            taskRow.State = "运行中";
            Console.WriteLine($"[HomeViewModel] {taskRow.PlateNo}/{taskRow.Sequence} 已分配到{taskRow.AssignedLine}号线, 忽略重复启动入队");
            return;
        }

        // 先构建完整工件数据；无论从总上料架还是人工中转架开始, 后续缓存都保存同一份WorkpieceCache。
        var wp = BuildWorkpieceCache(taskRow);
        if (taskRow.ProcessType == "省去双头镗工艺") Console.WriteLine($"[HomeViewModel] ⚡ 工件跳过双头镗工艺");

        if (taskRow.StartFromTransferRack)
        {
            TryStartFromTransferRack(taskRow, wp);
            return;
        }

        // 总上料架是1/2号线共用的唯一物理入口。
        // 任务先进入全局FIFO, 等M800=1确认当前物理板到位后, 再由唯一调度器按线路能力和运行状态派发。
        taskRow.AssignedLine = 0;
        EnqueueFrontDispatch(taskRow, wp);
    }

    private void ClearTaskRow(TaskRowViewModel row)
    {
        bool activeOrAssigned = row.IsRunning || row.AssignedLine > 0;
        if (activeOrAssigned)
        {
            var result = System.Windows.MessageBox.Show(
                "清除只会把任务从页面列表隐藏。\n\n不会停止设备、不会清线路缓存、不会清斜床/动平衡/研磨缓存、不会释放锁。\n如果该任务尚未派发到线路, 会同时从总上料架待派发队列移除。\n\n确认清除这行任务吗？",
                "确认清除任务显示",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (result != System.Windows.MessageBoxResult.Yes)
                return;
        }

        bool removedFromDispatch = RemoveFrontDispatch(row);
        if (removedFromDispatch)
            OnPropertyChanged(nameof(FrontDispatchCachedCount));

        if (TaskRows.Remove(row))
        {
            string effect = removedFromDispatch
                ? "已从页面和总上料架待派发FIFO清除"
                : "仅从页面隐藏, 不影响现场流程/缓存";
            Console.WriteLine($"[HomeViewModel] 清除任务显示：版号={row.PlateNo} 序号={row.Sequence} {effect}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  临时 MC 协议探测：页面加载时连一次，读状态写入卡片，排查通讯
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 页面加载时只探测共享MC缓存中的料架PLC（2.63/2.64/2.65）。
    /// 货叉、双头镗和斜床由生产引擎持有唯一连接，页面只消费引擎同步的状态，避免双连接。

    /// </summary>
    private async Task ProbeMcDevicesAsync()
    {
        Console.WriteLine("[ProbeMC] ═══════ 开始 MC 设备探测（页面加载时连一次）═══════");
        var cards = StationCards;

        // ── 1. 探测总上料架+中转架 192.168.2.63:9000 ──────────────────
        {
            try
            {
                using var cts = new CancellationTokenSource(5000);
                Console.WriteLine("[ProbeMC] [总上料架] STEP1 获取共享MC连接 192.168.2.63:9000 (timeout=5s)...");
                MitsubishiMcClient rackClient;
                try { rackClient = await _mcCache.GetOrCreateAsync("192.168.2.63", 9000, cts.Token); }
                catch (OperationCanceledException) { Console.WriteLine("[ProbeMC] [总上料架] ✘ TCP连接超时(5s) — PLC离线或IP/端口不对"); throw; }
                Console.WriteLine($"[ProbeMC] [总上料架] STEP2 共享MC连接可用 ✓ IsConnected={rackClient.IsConnected}");

                // 读 M800 起始 2 字 (M800~M831)
                Console.WriteLine("[ProbeMC] [总上料架] STEP3 发送MC读帧 M800 2字...");
                ReadResult result;
                try { result = await rackClient.ReadMAlignedWordAsync(800, 2, cts.Token); }
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
        }

        // ── 2. 探测 192.168.2.64:9000 (研磨下料架 ST710) ─────
        {
            try
            {
                using var cts = new CancellationTokenSource(5000);
                Console.WriteLine("[ProbeMC] [2.64下料架] 获取共享MC连接...");
                var client = await _mcCache.GetOrCreateAsync("192.168.2.64", 9000, cts.Token);
                Console.WriteLine("[ProbeMC] [2.64下料架] 共享MC连接可用 ✓");

                // 与GrindingFlowEngine保持一致: MC64下料架按对齐字读M720起1字；M720=1表示无板且允许放版。
                var result = await client.ReadMAlignedWordAsync(720, 1, cts.Token);
                ushort raw = (ushort)(result.IntValues.Length > 0 ? result.IntValues[0] : 0);
                Console.WriteLine($"[ProbeMC] [2.64下料架] ✔ 读成功 M720~M735=0x{raw:X4}");

                bool m720CanPlace = (raw & 1) != 0;              // M720=1: 下料架无板且允许放版
                bool m721PlaceDone = (raw & (1 << 1)) != 0;     // M721=1: 天车放版完成

                string unloadState = m720CanPlace ? "无板/允许放料" : "不可放料";
                var unloadBrush = m720CanPlace ? Brushes.Green : Brushes.Orange;
                if (cards.TryGetValue("ST013", out var c13)) { c13.ConnectedBrush = Brushes.LimeGreen; c13.Status1 = unloadState; c13.Status1Brush = unloadBrush; c13.Status2 = $"M720={(m720CanPlace ? 1 : 0)} M721={(m721PlaceDone ? 1 : 0)}"; c13.IpText = "192.168.2.64:9000"; }
                if (cards.TryGetValue("ST710", out var c710)) { c710.ConnectedBrush = Brushes.LimeGreen; c710.Status1 = unloadState; c710.Status1Brush = unloadBrush; c710.Status2 = $"M720={(m720CanPlace ? 1 : 0)} M721={(m721PlaceDone ? 1 : 0)}"; c710.IpText = "192.168.2.64:9000"; }
                Console.WriteLine("[ProbeMC] [2.64下料架] 卡片已更新: ST013✓ ST710✓");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProbeMC] [2.64下料架] ✘ 失败: {ex.GetType().Name} — {ex.Message}");
                if (cards.TryGetValue("ST013", out var c)) { c.ConnectedBrush = Brushes.Red; c.Status1 = "无法连接"; }
                if (cards.TryGetValue("ST710", out var c2)) { c2.ConnectedBrush = Brushes.Red; c2.Status1 = "无法连接"; }
            }
        }

        // ── 3. 探测 192.168.2.65:9000 (研磨机上料架 ST709 + 动平衡) ────
        {
            try
            {
                using var cts = new CancellationTokenSource(5000);
                Console.WriteLine("[ProbeMC] [2.65上料架] 获取共享MC连接...");
                var client = await _mcCache.GetOrCreateAsync("192.168.2.65", 9000, cts.Token);
                Console.WriteLine("[ProbeMC] [2.65上料架] 共享MC连接可用 ✓");

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
        }

        Console.WriteLine("[ProbeMC] ═══════ MC 设备探测结束（3路: 2.63✓ 2.64✓ 2.65✓）═══════");
    }


}

