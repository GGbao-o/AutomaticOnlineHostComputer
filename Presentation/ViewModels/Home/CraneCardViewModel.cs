using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// 单台天车卡片 ViewModel。
/// <para>
/// 通过 <see cref="CraneConnectionCache"/> 获取对应编号的天车 <see cref="CraneService"/>，
/// 启动后每 5 秒轮询 D5000~D5029 状态（<see cref="CraneStatus"/>），
/// 实时更新卡片上的 Line1~Line5 显示和连接指示灯颜色。
/// </para>
/// <para>
/// 卡片上 8 个按钮（急停/伺服通断/清除报警/充退磁/接液盘开关）直接调用 CraneService
/// 写 Modbus D4500~D4518 手动控制寄存器。
/// </para>
/// </summary>
public sealed class CraneCardViewModel : ObservableObject, IDisposable
{
    private readonly CraneConnectionCache _cache;
    private readonly int _craneNo;
    private readonly PositionUpdateService? _posSvc;
    private CraneService? _service;
    private CancellationTokenSource? _pollCts;

    // ── 重连退避 ──────────────────────────────────────────────────
    /// <summary>当前重连间隔（ms），成功后重置为 5000，失败后翻倍至上限 30000。</summary>
    private int _reconnectDelayMs = 5000;
    private const int ReconnectDelayInitMs = 5000;
    private const int ReconnectDelayMaxMs  = 30000;

    /// <param name="craneNo">天车编号 1~5</param>
    /// <param name="cache">已装载 IP 映射的天车连接缓存</param>
    public CraneCardViewModel(int craneNo, CraneConnectionCache cache, PositionUpdateService? posSvc = null)
    {
        _craneNo = craneNo;
        _cache = cache;
        _posSvc = posSvc;

        CraneName = $"{craneNo}号天车";

        EStopCommand = new AsyncRelayCommand(DoEStopAsync, nameof(EStopCommand));
        ServoPowerOffCommand = new AsyncRelayCommand(DoServoPowerOffAsync, nameof(ServoPowerOffCommand));
        ServoPowerOnCommand = new AsyncRelayCommand(DoServoPowerOnAsync, nameof(ServoPowerOnCommand));
        ClearAlarmCommand = new AsyncRelayCommand(DoClearAlarmAsync, nameof(ClearAlarmCommand));
        MagnetOnCommand = new AsyncRelayCommand(DoMagnetOnAsync, nameof(MagnetOnCommand));
        MagnetOffCommand = new AsyncRelayCommand(DoMagnetOffAsync, nameof(MagnetOffCommand));
        DrainOpenCommand = new AsyncRelayCommand(DoDrainOpenAsync, nameof(DrainOpenCommand));
        DrainCloseCommand = new AsyncRelayCommand(DoDrainCloseAsync, nameof(DrainCloseCommand));
    }

    /// <summary>天车名称（如 "1号线天车前"），首次连接后从缓存获取。</summary>
    public string CraneName { get; private set; }

    /// <summary>第 1 行：连接状态 / 故障信息</summary>
    private string _line1 = "未连接";
    public string Line1 { get => _line1; private set => SetField(ref _line1, value); }
    /// <summary>第 2 行：当前任务</summary>
    private string _line2 = "—";
    public string Line2 { get => _line2; private set => SetField(ref _line2, value); }
    /// <summary>第 3 行：自动/手动模式</summary>
    private string _line3 = "—";
    public string Line3 { get => _line3; private set => SetField(ref _line3, value); }
    /// <summary>第 4 行：运行条件</summary>
    private string _line4 = "—";
    public string Line4 { get => _line4; private set => SetField(ref _line4, value); }
    /// <summary>第 5 行：工件信息 + XYZ 坐标</summary>
    private string _line5 = "—";
    public string Line5 { get => _line5; private set => SetField(ref _line5, value); }

    /// <summary>连接指示灯颜色：灰=未连接, 绿=正常, 红=故障</summary>
    private Brush _connectedBrush = Brushes.Gray;
    public Brush ConnectedBrush { get => _connectedBrush; private set => SetField(ref _connectedBrush, value); }
    /// <summary>Line1 文字颜色</summary>
    private Brush _line1Brush = Brushes.Black;
    public Brush Line1Brush { get => _line1Brush; private set => SetField(ref _line1Brush, value); }
    private Brush _line2Brush = Brushes.Black;
    public Brush Line2Brush { get => _line2Brush; private set => SetField(ref _line2Brush, value); }
    private Brush _line3Brush = Brushes.Black;
    public Brush Line3Brush { get => _line3Brush; private set => SetField(ref _line3Brush, value); }
    private Brush _line4Brush = Brushes.Black;
    public Brush Line4Brush { get => _line4Brush; private set => SetField(ref _line4Brush, value); }
    private Brush _line5Brush = Brushes.Black;
    public Brush Line5Brush { get => _line5Brush; private set => SetField(ref _line5Brush, value); }

    /// <summary>急停按钮（D4518=1）</summary>
    public ICommand EStopCommand { get; }
    /// <summary>伺服断电（D4515=1）</summary>
    public ICommand ServoPowerOffCommand { get; }
    /// <summary>伺服通电（D4516=1）</summary>
    public ICommand ServoPowerOnCommand { get; }
    /// <summary>清除报警（D4514=1）</summary>
    public ICommand ClearAlarmCommand { get; }
    /// <summary>充磁（D4510=1）</summary>
    public ICommand MagnetOnCommand { get; }
    /// <summary>退磁（D4511=1）</summary>
    public ICommand MagnetOffCommand { get; }
    /// <summary>开接液盘（D4512=1）</summary>
    public ICommand DrainOpenCommand { get; }
    /// <summary>关接液盘（D4513=1）</summary>
    public ICommand DrainCloseCommand { get; }

    /// <summary>
    /// 启动天车卡片：连接 PLC → 首次读取状态 → 启动 5 秒轮询。
    /// 连接或首次读取失败会显示"未连接"但不抛异常，后续轮询会自动重连。
    /// </summary>
    public async Task StartAsync()
    {
        var info = _cache.GetCraneInfo(_craneNo);
        CraneName = info.Name;
        OnPropertyChanged(nameof(CraneName));

        Console.WriteLine($"[CraneCardVM] [{CraneName}] 启动：数据库IP={info.Ip}");
        _service = _cache.GetOrCreateService(_craneNo);

        try
        {
            Console.WriteLine($"[CraneCardVM] [{CraneName}] 开始连接...");
            await _service.ConnectAsync();
            Console.WriteLine($"[CraneCardVM] [{CraneName}] 连接成功，开始首次读取...");

            var first = await _service.ReadStatusAsync();
            if (first != null)
            {
                Console.WriteLine($"[CraneCardVM] [{CraneName}] 首次读取成功。");
                UpdateUiFromStatus(first);
            }
            else
            {
                Console.WriteLine($"[CraneCardVM] [{CraneName}] 首次读取失败（返回null）。");
                SetDisconnectedUi();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CraneCardVM] [{CraneName}] 初始连接/读取失败：{ex.Message}");
            SetDisconnectedUi();
        }

        _pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(_pollCts.Token);
        Console.WriteLine($"[CraneCardVM] [{CraneName}] 轮询启动，间隔5s。");
    }

    /// <summary>
    /// 轮询循环：连接检查 → 读取 D5000~D5029 → 更新 UI。
    /// 连接失败时指数退避（5s → 10s → 20s → 30s max），成功后重置回 5s。
    /// </summary>
    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _service ??= _cache.GetOrCreateService(_craneNo);

                // ── 重连逻辑 ──────────────────────────────────────────
                if (!_service.IsConnected)
                {
                    Console.WriteLine($"[CraneCardVM] [{CraneName}] 未连接，尝试重连...");
                    await _service.ConnectAsync(ct);
                    Console.WriteLine($"[CraneCardVM] [{CraneName}] ✔ 重连成功");
                    _reconnectDelayMs = ReconnectDelayInitMs;   // 成功后重置为 5s
                }

                // ── 读取状态 ──────────────────────────────────────────
                var status = await _service.ReadStatusAsync(ct);
                if (status != null)
                {
                    UpdateUiFromStatus(status);
                }
                else
                {
                    Console.WriteLine($"[CraneCardVM] [{CraneName}] 读取状态返回 null");
                    SetDisconnectedUi();
                }

                // 正常轮询间隔 5s
                await Task.Delay(ReconnectDelayInitMs, ct);
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine($"[CraneCardVM] [{CraneName}] 连接超时：{ex.Message}，{_reconnectDelayMs / 1000}s 后重试");
                SetDisconnectedUi();
                await Task.Delay(_reconnectDelayMs, ct);
                _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, ReconnectDelayMaxMs);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[CraneCardVM] [{CraneName}] 通信异常(IO)：{ex.Message}，{_reconnectDelayMs / 1000}s 后重试");
                SetDisconnectedUi();
                await Task.Delay(_reconnectDelayMs, ct);
                _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, ReconnectDelayMaxMs);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[CraneCardVM] [{CraneName}] 未知异常：{ex.Message}，{_reconnectDelayMs / 1000}s 后重试");
                SetDisconnectedUi();
                await Task.Delay(_reconnectDelayMs, ct);
                _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, ReconnectDelayMaxMs);
            }
        }
    }

    /// <summary>将 <see cref="CraneStatus"/> 快照映射到 UI 绑定属性（Line1~5 + 颜色）。</summary>
    private void UpdateUiFromStatus(CraneStatus s)
    {
        var hasFault = s.Fault != 0 || s.ServoAlarm != 0 || s.PlcAlarm != 0;
        Line1 = hasFault ? "已连接 | 故障" : "已连接，就绪";
        Line1Brush = hasFault ? Brushes.Red : Brushes.Green;
        ConnectedBrush = hasFault ? Brushes.Red : Brushes.LimeGreen;
        Line2 = s.Busy == 1 ? $"执行任务 #{s.CurrentTaskNo}" : "无任务";
        Line3 = s.Mode == 1 ? "自动模式" : (s.Mode == 2 ? "手动模式" : $"模式未知({s.Mode})");
        Line4 = s.RunConditionMissing != 0 ? $"条件缺失 0x{s.RunConditionMissing:X4}" : "运行条件满足";
        Line5 = $"{(s.HasRoller == 1 ? "有版" : "无版")} | X={s.XPos}  Y={s.YPos}  Z={s.ZPos}";

        // 异步更新当前位置到数据库（fire-and-forget，不阻塞轮询）
        if (_posSvc != null)
            _ = _posSvc.UpdateCranePositionAsync(CraneName, s.XPos, s.YPos, s.ZPos);
    }

    /// <summary>连接断开时重置所有显示为"—"、灯变灰。</summary>
    private void SetDisconnectedUi()
    {
        ConnectedBrush = Brushes.Gray;
        Line1 = "未连接";
        Line2 = "—";
        Line3 = "—";
        Line4 = "—";
        Line5 = "—";
    }

    /// <summary>确保天车已连接后返回服务实例（未连接则先 ConnectAsync）。</summary>
    private async Task<CraneService> EnsureConnectedAsync()
    {
        _service ??= _cache.GetOrCreateService(_craneNo);
        if (!_service.IsConnected)
        {
            Console.WriteLine($"[CraneCardVM] [{CraneName}] 写操作前检测到未连接，先重连...");
            await _service.ConnectAsync();
        }
        return _service;
    }

    private async Task DoEStopAsync() => await (await EnsureConnectedAsync()).EmergencyStopAsync();
    private async Task DoServoPowerOffAsync() => await (await EnsureConnectedAsync()).ServoPowerOffAsync();
    private async Task DoServoPowerOnAsync() => await (await EnsureConnectedAsync()).ServoPowerOnAsync();
    private async Task DoClearAlarmAsync() => await (await EnsureConnectedAsync()).ClearAlarmAsync();
    private async Task DoMagnetOnAsync() => await (await EnsureConnectedAsync()).MagnetOnAsync();
    private async Task DoMagnetOffAsync() => await (await EnsureConnectedAsync()).MagnetOffAsync();
    private async Task DoDrainOpenAsync() => await (await EnsureConnectedAsync()).DrainOpenAsync();
    private async Task DoDrainCloseAsync() => await (await EnsureConnectedAsync()).DrainCloseAsync();

    /// <summary>
    /// 停止轮询并断开天车连接。释放 TCP 连接和 Modbus 客户端资源，
    /// 避免 PLC 连接数耗尽（通常仅支持 4~8 个并发连接）。
    /// </summary>
    public void Dispose()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        // 断开 TCP 连接释放 ModbusTcpClient 持有的 TcpClient/SemaphoreSlim
        _ = _service?.DisconnectAsync();
        Console.WriteLine($"[CraneCardVM] [{CraneName}] 已释放资源");
    }
}

/// <summary>
/// 异步命令（防重入）：执行期间 CanExecute=false，防止按钮连点导致并发写寄存器。
/// </summary>
internal sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly string _name;
    private bool _isRunning;

    public AsyncRelayCommand(Func<Task> execute, string name)
    {
        _execute = execute;
        _name = name;
    }

    public bool CanExecute(object? parameter) => !_isRunning;

    public async void Execute(object? parameter)
    {
        if (_isRunning) return;
        _isRunning = true;

        // CanExecuteChanged 的订阅方（如 WPF CommandManager）可能抛异常，
        // async void 中未捕获异常会直接崩整个进程，必须 try 保护
        try { CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Console.WriteLine($"[AsyncRelayCommand] CanExecuteChanged异常：{ex.Message}"); }

        Console.WriteLine($"[AsyncRelayCommand] 执行命令：{_name}");
        try { await _execute(); }
        catch (Exception ex) { Console.WriteLine($"[AsyncRelayCommand] 命令异常：{ex.Message}"); }
        finally
        {
            _isRunning = false;
            try { CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Console.WriteLine($"[AsyncRelayCommand] CanExecuteChanged异常：{ex.Message}"); }
        }
    }

    public event EventHandler? CanExecuteChanged;
}
