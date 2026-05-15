
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
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
    private readonly ManipulatorConnectionCache _manipulatorCache;

    /// <summary>页面是否正在加载（绑定到启动动画）</summary>
    // ═══════════════════════════════════════════════════════════════
    //  研磨流程引擎控制
    // ═══════════════════════════════════════════════════════════════

    /// <summary>启动/暂停研磨自动流程</summary>
    private async Task GrindingToggleAsync()
    {
        if (_grindingEngine == null)
        {
            Console.WriteLine("[HomeViewModel] ✘ 研磨引擎未初始化，无法启动");
            return;
        }

        if (_isGrindingRunning)
        {
            Console.WriteLine("[HomeViewModel] ▶ 点击按钮【暂停研磨】→ 急停天车");
            await _grindingEngine.PauseAsync();
            IsGrindingRunning = false;
            OnPropertyChanged(nameof(CachedWorkpieceCount));
        }
        else
        {
            Console.WriteLine("[HomeViewModel] ▶ 点击按钮【启动研磨】→ 开始自动流程");
            _grindingEngine.Resume();  // 如果是暂停后恢复
            if (!_grindingEngine.IsRunning)
                _grindingEngine.Start();
            IsGrindingRunning = true;
            OnPropertyChanged(nameof(CachedWorkpieceCount));
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
        OnPropertyChanged(nameof(CachedWorkpieceCount));
        await Task.CompletedTask;
    }

    /// <summary>清空工件缓存</summary>
    private async Task ClearCacheAsync()
    {
        Console.WriteLine("[HomeViewModel] ▶ 点击按钮【清空缓存】");
        _grindingEngine?.ClearCache();
        OnPropertyChanged(nameof(CachedWorkpieceCount));
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

    public HomeViewModel(ManagementQueryService queryService, PositionUpdateService positionService)
    {
        Console.WriteLine("HomeViewModel页面启动");
        _queryService = queryService;
        _positionService = positionService;
        _craneCache = new CraneConnectionCache();
        _manipulatorCache = new ManipulatorConnectionCache();

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

        GrindingToggleCommand = new AsyncRelayCommand(GrindingToggleAsync, nameof(GrindingToggleCommand));
        WriteCacheCommand = new AsyncRelayCommand(WriteCacheAsync, nameof(WriteCacheCommand));
        ClearCacheCommand = new AsyncRelayCommand(ClearCacheAsync, nameof(ClearCacheCommand));

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

    // ═══════════════════════════════════════════════════════════════
    //  研磨自动流程引擎
    // ═══════════════════════════════════════════════════════════════

    private GrindingFlowEngine? _grindingEngine;

    /// <summary>是否正在运行研磨自动流程</summary>
    private bool _isGrindingRunning;
    public bool IsGrindingRunning
    {
        get => _isGrindingRunning;
        set { if (SetField(ref _isGrindingRunning, value)) OnPropertyChanged(nameof(GrindingToggleText)); }
    }

    /// <summary>启动/暂停按钮文本</summary>
    public string GrindingToggleText => _isGrindingRunning ? "暂停研磨" : "启动研磨";

    /// <summary>已缓存工件数量</summary>
    public int CachedWorkpieceCount => _grindingEngine?.CachedCount ?? 0;

    /// <summary>缓存工件直径（mm）</summary>
    private int _cachedDiameter = 200;
    public int CachedDiameter { get => _cachedDiameter; set => SetField(ref _cachedDiameter, value); }

    /// <summary>缓存工件版孔（1=大孔 2=小孔）</summary>
    private int _cachedBoreType = 1;
    public int CachedBoreType { get => _cachedBoreType; set => SetField(ref _cachedBoreType, value); }

    /// <summary>缓存工件长度（mm）</summary>
    private int _cachedLength = 1000;
    public int CachedLength { get => _cachedLength; set => SetField(ref _cachedLength, value); }

    /// <summary>启动/暂停研磨流程</summary>
    public ICommand GrindingToggleCommand { get; }
    /// <summary>将工件写入缓存</summary>
    public ICommand WriteCacheCommand { get; }
    /// <summary>清空工件缓存</summary>
    public ICommand ClearCacheCommand { get; }

    /// <summary>
    /// 主页面任务表数据源（左侧DataGrid绑定）。
    /// </summary>
    public ObservableCollection<TaskRowViewModel> TaskRows { get; } = new();

    /*异步获取机械手 天车的ip地址 然后存缓存中*/
    public async Task LoadAsync()
    {
        IsLoading = true;
        Console.WriteLine("[HomeViewModel] ========== LoadAsync 开始 ==========");

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
        _grindingEngine = new GrindingFlowEngine(_craneCache, MotionConfig.Load(), grindingCoords);
        // 注册 4 台研磨机卡片的状态回调（引擎通知充磁/退磁到 Line5）
        _grindingEngine.RegisterStatusCallback("ST701", status => Grinder1.SetFlowStatus(status));
        _grindingEngine.RegisterStatusCallback("ST702", status => Grinder2.SetFlowStatus(status));
        _grindingEngine.RegisterStatusCallback("ST703", status => Grinder3.SetFlowStatus(status));
        _grindingEngine.RegisterStatusCallback("ST704", status => Grinder4.SetFlowStatus(status));
        Console.WriteLine($"[HomeViewModel] 研磨流程引擎已创建（{grindingCoords.Count} 个工位坐标 + 4个卡片状态回调）");

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
            var rawIp = string.IsNullOrWhiteSpace(row.Ip) ? "未配置IP" : row.Ip.Trim();
            var ip = isGrinder ? "未配置IP" : rawIp;
            var card = new StationCardViewModel(code, string.IsNullOrWhiteSpace(row.Name) ? code : row.Name.Trim(),
                ip, row.Port > 0 ? row.Port : 502, typeName.Length > 0 ? typeName : "ModbusTCP");
            newCards[code] = card;
            if (isGrinder) card.IpText = rawIp;
        }
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
        _ = Task.Run(async () =>
        {
            var tasks = newCards.Values.Where(c => c.HasIp).Select(c => SafeStartStationAsync(c)).ToList();
            if (tasks.Count > 0) await Task.WhenAll(tasks);
            Console.WriteLine($"[HomeViewModel] {tasks.Count} 个站卡后台连接完成");
        });
        _ = Task.Run(async () =>
        {
            await StartGrinderConnectionsAsync(newCards);
            Console.WriteLine("[HomeViewModel] 研磨机GrinderPoll后台连接完成（已注入共享服务到卡片）");
        });

        Console.WriteLine("[HomeViewModel] ========== LoadAsync 完成 ==========");
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
            _ = GrinderPollLoopAsync(code, name, gtype, ip, card, grinderCard);
        }
    }

    /// <summary>研磨机轮询循环：连接 → 5s读状态 → 更新站卡 → 断开重连。</summary>
    private static async Task GrinderPollLoopAsync(string code, string name,
        PlcGrinderService.GrinderType gtype, string ip, StationCardViewModel stationCard,
        GrinderCardViewModel grinderCard)
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
                    Console.WriteLine($"[GrinderPoll] [{name}] {(svc == null ? "首次连接" : "重连")} IP={ip}...");
                    svc?.Dispose();
                    svc = new PlcGrinderService(name, ip, gtype);
                    await svc.ConnectAsync();
                    Console.WriteLine($"[GrinderPoll] [{name}] ✔ 连接成功 IP={ip} Type={gtype}");
                    delay = 5000;
                    stationCard.ConnectedBrush = System.Windows.Media.Brushes.LimeGreen;
                    // 注入共享服务到研磨机卡片，避免卡片另建连接
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
    public void AddTask(TaskRowViewModel row)
    {
        TaskRows.Add(row);
        Console.WriteLine($"[HomeViewModel] 新增任务：版号={row.PlateNo} 序号={row.Sequence} 工序={row.Step}");

        // 构建工件上下文并投入引擎
        var ctx = new Domain.Models.WorkpieceContext
        {
            PlateNo            = row.PlateNo,
            Sequence           = row.Sequence,
            Length             = row.Length,
            Diameter           = row.Diameter,
            PlugHole           = row.PlugHole,
            LeftPlugThickness  = row.LeftPlugThickness,
            RightPlugThickness = row.RightPlugThickness,
            MarkingContent     = row.MarkingContent,
            ProcessType        = row.ProcessType,
        };

        // 绑定 UI 行到工件上下文（引擎推进阶段时自动更新 DataGrid）
        ctx.UiRow = row;

        _flowEngine.EnqueueWorkpiece(ctx);

        // 如果引擎还没启动，启动它
        if (!_flowEngine.IsRunning)
        {
            _flowEngine.Start();
            Console.WriteLine("[HomeViewModel] 流程引擎已启动");
        }

        // 更新 UI 状态
        row.State = "已入队";
        row.Step  = ctx.CurrentStepText;
    }
}

