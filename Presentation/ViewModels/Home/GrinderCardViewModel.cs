using System;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// 研磨机卡片 ViewModel（主页面黄台总览区）。
/// <para>不自行创建 Modbus 连接，由 GrinderPoll 注入共享 PlcGrinderService，
/// 避免同一 IP 被卡片和 GrinderPoll 双重连接。</para>
/// <para>8 个按钮：急停/清除报警（占位）+ 读状态 + 5 个流程握手信号（手动调试用）。</para>
/// </summary>
public sealed class GrinderCardViewModel : ObservableObject, IDisposable
{
    private readonly string _name;
    private readonly string _ip;
    private readonly PlcGrinderService.GrinderType _type;
    private PlcGrinderService? _svc;
    private bool _disposed;

    public GrinderCardViewModel(string name, string ip, PlcGrinderService.GrinderType type)
    {
        _name = name;
        _ip   = ip;
        _type = type;
        Title = name;

        // 按钮1~3：急停 / 清除报警 / 读状态
        EStopCommand        = new AsyncRelayCommand(EStopAsync,        nameof(EStopCommand));
        ClearAlarmCommand   = new AsyncRelayCommand(ClearAlarmAsync,   nameof(ClearAlarmCommand));
        ReadStatusCommand   = new AsyncRelayCommand(ReadStatusOnceAsync, nameof(ReadStatusCommand));

        // 按钮4~8：5 个流程握手信号（手动调试用，3 秒长信号）
        DataSentDoneCommand  = new AsyncRelayCommand(DataSentDoneAsync,  nameof(DataSentDoneCommand));
        LoadInPlaceCommand   = new AsyncRelayCommand(LoadInPlaceAsync,   nameof(LoadInPlaceCommand));
        LoadDoneCommand      = new AsyncRelayCommand(LoadDoneAsync,      nameof(LoadDoneCommand));
        UnloadInPlaceCommand = new AsyncRelayCommand(UnloadInPlaceAsync, nameof(UnloadInPlaceCommand));
        UnloadDoneCommand    = new AsyncRelayCommand(UnloadDoneAsync,    nameof(UnloadDoneCommand));

        SetDisconnected();
        Console.WriteLine($"[GrinderCardVM] [{_name}] 创建 Type={type} IP={ip}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  绑定属性
    // ═══════════════════════════════════════════════════════════════

    public string Title { get; }

    private string _line1 = "未连接";
    public string Line1 { get => _line1; private set => SetField(ref _line1, value); }
    private string _line2 = "—";
    public string Line2 { get => _line2; private set => SetField(ref _line2, value); }
    private string _line3 = "—";
    public string Line3 { get => _line3; private set => SetField(ref _line3, value); }
    private string _line4 = "—";
    public string Line4 { get => _line4; private set => SetField(ref _line4, value); }
    private string _line5 = "—";
    public string Line5 { get => _line5; private set => SetField(ref _line5, value); }

    private Brush _connectedBrush = Brushes.Gray;
    public Brush ConnectedBrush { get => _connectedBrush; private set => SetField(ref _connectedBrush, value); }
    private Brush _line1Brush = Brushes.Black;
    public Brush Line1Brush { get => _line1Brush; private set => SetField(ref _line1Brush, value); }
    private Brush _line4Brush = Brushes.Black;
    public Brush Line4Brush { get => _line4Brush; private set => SetField(ref _line4Brush, value); }

    // ── 命令属性 ──────────────────────────────────────────────────
    public ICommand EStopCommand { get; }
    public ICommand ClearAlarmCommand { get; }
    public ICommand ReadStatusCommand { get; }
    public ICommand DataSentDoneCommand { get; }
    public ICommand LoadInPlaceCommand { get; }
    public ICommand LoadDoneCommand { get; }
    public ICommand UnloadInPlaceCommand { get; }
    public ICommand UnloadDoneCommand { get; }

    // ═══════════════════════════════════════════════════════════════
    //  注入共享服务（由 GrinderPoll 调用）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// GrinderPoll 在成功连接后注入共享的 PlcGrinderService，
    /// 卡片按钮通过该服务发送握手信号。
    /// </summary>
    public void SetSharedService(PlcGrinderService svc)
    {
        _svc = svc;
        Console.WriteLine($"[GrinderCardVM] [{_name}] 共享服务已注入");
    }

    /// <summary>GrinderPoll 连接断开时通知卡片显示未连接状态。</summary>
    public void SetDisconnected()
    {
        _svc = null;
        ConnectedBrush = Brushes.Gray;
        Line1 = "未连接";
        Line2 = "—";
        Line3 = "—";
        Line4 = _type == PlcGrinderService.GrinderType.TypeA ? "西门子PLC (TypeA)" : "新代数控 (TypeB)";
        Line5 = "等待重连...";
    }

    // ═══════════════════════════════════════════════════════════════
    //  状态更新（由 GrinderPoll 轮询后调用）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>根据已读取的快照更新卡片 5 行显示（由 GrinderPoll 调用）。</summary>
    public void UpdateTypeA(PlcGrinderService.TypeAStatusSnapshot snapshot)
    {
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

        ConnectedBrush = fault ? Brushes.Red : Brushes.LimeGreen;
        Line1Brush = fault ? Brushes.Red : Brushes.Green;
        Line1 = fault ? "已连接 | ⚠ 故障" : "已连接，就绪";

        Line2 = reqUnld ? "请求下料 → 等待天车取料" :
                reqLoad ? "请求上料 → 等待天车送料" :
                clamped ? "锁紧完成 → 天车可上移" :
                reqData ? "请求数据 → 等待下发参数" :
                unclamp ? "松开完成 → 天车可上移" :
                busy    ? "加工中" :
                "空闲";

        var flags = new System.Collections.Generic.List<string> { door ? "门开=1" : "门关=0" };
        if (stone1) flags.Add("磨石1报警✘");
        if (stone2) flags.Add("磨石2报警✘");
        Line3 = string.Join(" | ", flags);

        Line4 = "西门子PLC (TypeA)";
        Line4Brush = Brushes.DarkBlue;
        Line5 = $"DI=0x{snapshot.RawDI:X4} 心跳={snapshot.Heartbeat}";
    }

    public void UpdateTypeB(int machineStatus, bool reqData, bool reqLoad, bool clamped,
        bool reqUnld, bool unclamp, bool busy, bool door)
    {
        ConnectedBrush = machineStatus == 2 ? Brushes.Red : Brushes.LimeGreen;
        Line1Brush = machineStatus == 2 ? Brushes.Red : Brushes.Green;
        Line1 = machineStatus == 2 ? "已连接 | ⚠ 报警" : "已连接，就绪";

        Line2 = reqUnld ? "请求下料 → 等待天车取料" :
                reqLoad ? "请求上料 → 等待天车送料" :
                clamped ? "锁紧完成 → 天车可上移" :
                reqData ? "请求数据 → 等待下发参数" :
                unclamp ? "松开完成 → 天车可上移" :
                busy    ? "加工中" :
                machineStatus == 0 ? "空闲" : $"忙碌中({machineStatus})";

        Line3 = door ? "门开=1" : "门关=0";

        Line4 = "新代数控 (TypeB)";
        Line4Brush = Brushes.DarkGreen;
        Line5 = $"R7308={machineStatus} 门={(door ? "开" : "关")}";
    }

    // ═══════════════════════════════════════════════════════════════
    //  按钮 1 — 急停（占位）
    // ═══════════════════════════════════════════════════════════════

    private async Task EStopAsync()
    {
        Console.WriteLine($"[GrinderCardVM] [{_name}] ▶ 急停按钮（占位：研磨机点位表无独立急停寄存器）");
        if (_svc == null || !_svc.IsConnected)
        {
            Console.WriteLine($"[GrinderCardVM] [{_name}] ✘ 未连接，急停无效");
            return;
        }
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    //  按钮 2 — 清除报警（占位）
    // ═══════════════════════════════════════════════════════════════

    private async Task ClearAlarmAsync()
    {
        Console.WriteLine($"[GrinderCardVM] [{_name}] ▶ 清除报警按钮（占位：报警位只读）");
        if (_svc == null || !_svc.IsConnected)
        {
            Console.WriteLine($"[GrinderCardVM] [{_name}] ✘ 未连接，清除无效");
            return;
        }
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    //  按钮 3 — 手动读状态
    // ═══════════════════════════════════════════════════════════════

    private async Task ReadStatusOnceAsync()
    {
        if (_svc == null || !_svc.IsConnected)
        {
            Console.WriteLine($"[GrinderCardVM] [{_name}] ✘ 未连接，无法读取");
            return;
        }
        Console.WriteLine($"[GrinderCardVM] [{_name}] ▶ 手动读状态");
        await _svc.ReadAllStatusAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  按钮 4~8 — 流程握手信号（手动调试，3 秒长信号）
    // ═══════════════════════════════════════════════════════════════

    private async Task DataSentDoneAsync()
    {
        if (!EnsureConnected("数据传输完成")) return;
        Console.WriteLine($"[GrinderCardVM] [{_name}] ▶ 手动触发：数据传输完成（3s长信号）");
        await _svc!.SetDataSentDoneAsync();
        Console.WriteLine($"[GrinderCardVM] [{_name}] ✔ 数据传输完成 已发送");
    }
    private async Task LoadInPlaceAsync()
    {
        if (!EnsureConnected("上料到达")) return;
        Console.WriteLine($"[GrinderCardVM] [{_name}] ▶ 手动触发：上料到达锁紧位置（3s长信号）");
        await _svc!.SetLoadInPlaceAsync();
        Console.WriteLine($"[GrinderCardVM] [{_name}] ✔ 上料到达 已发送");
    }
    private async Task LoadDoneAsync()
    {
        if (!EnsureConnected("上料完成")) return;
        Console.WriteLine($"[GrinderCardVM] [{_name}] ▶ 手动触发：上料完成（3s长信号）");
        await _svc!.SetLoadDoneAsync();
        Console.WriteLine($"[GrinderCardVM] [{_name}] ✔ 上料完成 已发送");
    }
    private async Task UnloadInPlaceAsync()
    {
        if (!EnsureConnected("下料到达")) return;
        Console.WriteLine($"[GrinderCardVM] [{_name}] ▶ 手动触发：下料到达位置（3s长信号）");
        await _svc!.SetUnloadInPlaceAsync();
        Console.WriteLine($"[GrinderCardVM] [{_name}] ✔ 下料到达 已发送");
    }
    private async Task UnloadDoneAsync()
    {
        if (!EnsureConnected("下料完成")) return;
        Console.WriteLine($"[GrinderCardVM] [{_name}] ▶ 手动触发：下料完成（3s长信号）");
        await _svc!.SetUnloadDoneAsync();
        Console.WriteLine($"[GrinderCardVM] [{_name}] ✔ 下料完成 已发送");
    }

    /// <summary>写入研磨参数（直径/版孔/长度）到 PLC。由 HomeViewModel 面板调用。</summary>
    public async Task<bool> WriteParamsAsync(int diameter, int boreType, int length)
    {
        if (_svc == null || !_svc.IsConnected)
        {
            Console.WriteLine($"[GrinderCardVM] [{_name}] ✘ 写参数失败：未连接");
            return false;
        }
        Console.WriteLine($"[GrinderCardVM] [{_name}] ▶ 写入研磨参数 直径={diameter} 版孔={boreType} 长度={length}");
        await _svc.SendRollerParamsAsync(diameter, boreType, length);
        Console.WriteLine($"[GrinderCardVM] [{_name}] ✔ 研磨参数写入完成");
        return true;
    }

    private bool EnsureConnected(string action)
    {
        if (_svc == null || !_svc.IsConnected)
        {
            Console.WriteLine($"[GrinderCardVM] [{_name}] ✘ 未连接，{action}无效");
            return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Console.WriteLine($"[GrinderCardVM] [{_name}] 已释放");
    }
}
