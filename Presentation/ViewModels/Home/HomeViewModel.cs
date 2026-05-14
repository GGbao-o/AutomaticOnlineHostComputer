
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
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

        Console.WriteLine("[HomeViewModel] 开始启动5台天车卡片：连接成功后读取，轮询5s...");
        await Task.WhenAll(
            SafeStartAsync(Crane1F),
            SafeStartAsync(Crane1R),
            SafeStartAsync(Crane2F),
            SafeStartAsync(Crane2R),
            SafeStartAsync(CraneGL)
        );

        Console.WriteLine("[HomeViewModel] 开始启动3台机械手卡片：连接成功后读取，轮询5s...");
        await Task.WhenAll(
            SafeStartManipulatorAsync(Manipulator1),
            SafeStartManipulatorAsync(Manipulator2),
            SafeStartManipulatorAsync(Manipulator3)
        );

        // ── 装载工位坐标到引擎 ──────────────────────────────────────
        _flowEngine.LoadStationCoords(machineRows);
        Console.WriteLine("[HomeViewModel] 引擎已装载工位坐标缓存");

        // ── 启动研磨机卡片（IP从已缓存的IpMap取，不重复查库）───────
        Console.WriteLine("[HomeViewModel] 开始启动4台研磨机卡片...");
        Grinder1 = await CreateAndStartGrinderAsync("ST701", "研磨机1(新代)",   PlcGrinderService.GrinderType.TypeB);
        Grinder2 = await CreateAndStartGrinderAsync("ST702", "研磨机2(新代)",   PlcGrinderService.GrinderType.TypeB);
        Grinder3 = await CreateAndStartGrinderAsync("ST703", "研磨机3(西门子)", PlcGrinderService.GrinderType.TypeA);
        Grinder4 = await CreateAndStartGrinderAsync("ST704", "研磨机4(西门子)", PlcGrinderService.GrinderType.TypeA);
        OnPropertyChanged(nameof(Grinder1));
        OnPropertyChanged(nameof(Grinder2));
        OnPropertyChanged(nameof(Grinder3));
        OnPropertyChanged(nameof(Grinder4));

        // ── 初始化全厂状态卡片 ──────────────────────────────────────
        Console.WriteLine("[HomeViewModel] 开始初始化全厂状态总览卡片...");

        // 必须新建字典替换（不能 Clear+Add），否则 WPF 绑定引擎不刷新
        var newCards = new Dictionary<string, StationCardViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in machineRows)
        {
            // 没有站号的跳过
            if (string.IsNullOrWhiteSpace(row.StationCode)) continue;
            var code = row.StationCode.Trim().ToUpperInvariant();

            // 天车和机械手已在独立卡片中管理，状态总览区不重复
            var typeName = row.TypeName?.Trim() ?? string.Empty;
            if (typeName == "天车" || typeName == "机械手") continue;

            // 研磨机由 GrinderPollLoopAsync 独占 PlcGrinderService 连接，
            // 站卡仍创建（XAML绑定需要），但IP留空不启动通用Modbus轮询，避免双连接互踢
            bool isGrinder = code == "ST701" || code == "ST702" || code == "ST703" || code == "ST704";
            var rawIp = string.IsNullOrWhiteSpace(row.Ip) ? "未配置IP" : row.Ip.Trim();
            var ip = isGrinder ? "未配置IP" : rawIp; // 研磨机假装没IP，不启动StationCard轮询
            var card = new StationCardViewModel(
                code,
                string.IsNullOrWhiteSpace(row.Name) ? code : row.Name.Trim(),
                ip,
                row.Port > 0 ? row.Port : 502,
                typeName.Length > 0 ? typeName : "ModbusTCP");

            newCards[code] = card;
            // 研磨机站卡IP用于显示，实际连接由GrinderPollLoopAsync管理
            if (isGrinder) card.IpText = rawIp;
            Console.WriteLine($"[HomeViewModel] 站卡 {code} {card.Title} IP={ip}" + (isGrinder ? "（研磨机，由GrinderPoll独占）" : ""));
        }

        // 替换字典 → 触发 WPF 全局刷新所有站卡绑定
        StationCards = newCards;

        // 启动所有有 IP 的站卡轮询（并发）
        var startTasks = newCards.Values
            .Where(c => c.HasIp)
            .Select(c => SafeStartStationAsync(c))
            .ToList();
        if (startTasks.Count > 0)
        {
            await Task.WhenAll(startTasks);
            Console.WriteLine($"[HomeViewModel] {startTasks.Count} 个站卡轮询已启动");
        }

        // ── 研磨机自动连接（4台：ST701/ST702新代 + ST703/ST704西门子） ──
        await StartGrinderConnectionsAsync(newCards);

        Console.WriteLine("[HomeViewModel] 主页面全部功能启动流程结束。");
        OnPropertyChanged(nameof(IpMap));
        IsLoading = false;
        Console.WriteLine("[HomeViewModel] ========== LoadAsync 完成，页面就绪 ==========");
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

    /// <summary>从IpMap缓存取IP，创建并启动研磨机卡片。</summary>
    private async Task<GrinderCardViewModel> CreateAndStartGrinderAsync(
        string stationCode, string name, PlcGrinderService.GrinderType type)
    {
        string ip = IpMap.TryGetValue(stationCode, out var v) && v != "未配置IP" ? v : "";
        if (string.IsNullOrWhiteSpace(ip))
        {
            Console.WriteLine($"[GrinderCard] {name} ({stationCode}) IP未配置（IpMap缓存中不存在），创建空卡片");
            return new GrinderCardViewModel(name, "", type);
        }

        Console.WriteLine($"[GrinderCard] {name} ({stationCode}) 从IpMap缓存取IP={ip}");
        var card = new GrinderCardViewModel(name, ip, type);
        try { await card.StartAsync(); }
        catch (Exception ex) { Console.WriteLine($"[GrinderCard] [{name}] 启动异常：{ex.Message}"); }
        return card;
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
        var grinderDefs = new (string code, string name, PlcGrinderService.GrinderType type)[]
        {
            ("ST701", "研磨机1(新代)",   PlcGrinderService.GrinderType.TypeB),
            ("ST702", "研磨机2(新代)",   PlcGrinderService.GrinderType.TypeB),
            ("ST703", "研磨机3(西门子)", PlcGrinderService.GrinderType.TypeA),
            ("ST704", "研磨机4(西门子)", PlcGrinderService.GrinderType.TypeA),
        };

        foreach (var (code, name, gtype) in grinderDefs)
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
            _ = GrinderPollLoopAsync(code, name, gtype, ip, card);
        }
    }

    /// <summary>研磨机轮询循环：连接 → 5s读状态 → 更新站卡 → 断开重连。</summary>
    private static async Task GrinderPollLoopAsync(string code, string name,
        PlcGrinderService.GrinderType gtype, string ip, StationCardViewModel card)
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
                    card.ConnectedBrush = System.Windows.Media.Brushes.LimeGreen;
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

                    card.ConnectedBrush = fault ? System.Windows.Media.Brushes.Red
                        : System.Windows.Media.Brushes.LimeGreen;
                    card.Status1Brush = fault ? System.Windows.Media.Brushes.Red
                        : System.Windows.Media.Brushes.Green;
                    card.Status1 = fault ? "故障" : (busy ? "加工中" : (reqData ? "请求数据" : "空闲"));
                    card.Status2 = fault ? $"DI=0x{di:X4}" : "西门子PLC";

                    Console.WriteLine($"[GrinderPoll] [{name}] #{cycleCount} TypeA DI=0x{di:X4} " +
                        $"报警={fault} 加工={busy} 请求数据={reqData} 磨石1={stone1} 磨石2={stone2}");
                }
                else
                {
                    int status   = await svc.GetMachineStatusAsync();
                    bool reqData = await svc.IsRequestDataAsync();
                    bool busy    = await svc.IsMachiningAsync();
                    bool door    = await svc.IsDoorOpenAsync();

                    card.ConnectedBrush = status == 2 ? System.Windows.Media.Brushes.Red
                        : System.Windows.Media.Brushes.LimeGreen;
                    card.Status1Brush = status == 2 ? System.Windows.Media.Brushes.Red
                        : System.Windows.Media.Brushes.Green;
                    card.Status1 = status == 2 ? "报警" : (busy ? "加工中" : (reqData ? "请求数据" : "空闲"));
                    card.Status2 = status == 2 ? $"R7308={status}" : (door ? "安全门开" : "新代数控");

                    Console.WriteLine($"[GrinderPoll] [{name}] #{cycleCount} TypeB R7308={status} " +
                        $"加工={busy} 请求数据={reqData} 门开={door}");
                }

                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GrinderPoll] [{name}] #{cycleCount} ✘ 异常：{ex.GetType().Name} — {ex.Message}");
                card.Status1 = "连接失败";
                card.Status2 = "等待重连...";
                card.ConnectedBrush = System.Windows.Media.Brushes.Gray;
                card.Status1Brush = System.Windows.Media.Brushes.Gray;
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

