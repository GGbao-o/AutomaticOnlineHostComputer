using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using AutomaticOnlineHostComputer.Communication.DeviceAddresses;
using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// 单台小机械手卡片 ViewModel。
/// <para>
/// 机械手 PLC 寄存器地址与天车完全相同（汇川 PLC Modbus TCP），因此复用 <see cref="CraneService"/>
/// 和 <see cref="CraneStatus"/> 数据结构，不在代码层面区分天车/机械手。
/// </para>
/// <para>
/// 启动后每 5 秒轮询状态，卡片显示连接状态/任务/模式/条件/坐标。
/// 按钮功能仅开放急停、伺服通断、清除报警、Y±、Z±（无 X 轴、无充退磁、无接液盘）。
/// </para>
/// <para>
/// 【已知局限】点动距离硬编码为 1000mm，无 UI 配置入口，与天车手动控制面板不一致。
/// </para>
/// </summary>
public sealed class ManipulatorCardViewModel : ObservableObject, IDisposable
{
    private readonly ManipulatorConnectionCache _cache;
    private readonly int _no;
    private CraneService? _service;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    // ── 重连退避 ──────────────────────────────────────────────────
    /// <summary>当前重连间隔（ms），成功后重置为 5000，失败后翻倍至上限 30000。</summary>
    private int _reconnectDelayMs = 5000;
    private const int ReconnectDelayInitMs = 5000;
    private const int ReconnectDelayMaxMs  = 30000;

    /// <param name="no">机械手编号 1~3</param>
    /// <param name="cache">已装载 IP 映射的机械手连接缓存</param>
    public ManipulatorCardViewModel(int no, ManipulatorConnectionCache cache)
    {
        _no = no;
        _cache = cache;
        Name = $"机械手{no}";

        EStopCommand = new AsyncRelayCommand(EStopAsync, nameof(EStopCommand));
        ServoPowerOnCommand = new AsyncRelayCommand(ServoPowerOnAsync, nameof(ServoPowerOnCommand));
        ServoPowerOffCommand = new AsyncRelayCommand(ServoPowerOffAsync, nameof(ServoPowerOffCommand));
        ClearAlarmCommand = new AsyncRelayCommand(ClearAlarmAsync, nameof(ClearAlarmCommand));
        YPlusCommand = new AsyncRelayCommand(() => MoveYAsync(true), nameof(YPlusCommand));
        YMinusCommand = new AsyncRelayCommand(() => MoveYAsync(false), nameof(YMinusCommand));
        ZPlusCommand = new AsyncRelayCommand(() => MoveZAsync(true), nameof(ZPlusCommand));
        ZMinusCommand = new AsyncRelayCommand(() => MoveZAsync(false), nameof(ZMinusCommand));
    }

    /// <summary>机械手名称（如"机械手1"），首次连接后更新。</summary>
    public string Name { get; private set; }

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
    /// <summary>第 5 行：Y/Z 坐标</summary>
    private string _line5 = "—";
    public string Line5 { get => _line5; private set => SetField(ref _line5, value); }

    /// <summary>连接指示灯：灰=未连接, 绿=正常, 红=故障</summary>
    private Brush _connectedBrush = Brushes.Gray;
    public Brush ConnectedBrush { get => _connectedBrush; private set => SetField(ref _connectedBrush, value); }
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

    /// <summary>急停（D4518=1）</summary>
    public ICommand EStopCommand { get; }
    /// <summary>伺服通电（D4516=1）</summary>
    public ICommand ServoPowerOnCommand { get; }
    /// <summary>伺服断电（D4515=1）</summary>
    public ICommand ServoPowerOffCommand { get; }
    /// <summary>清除报警（D4514=1）</summary>
    public ICommand ClearAlarmCommand { get; }
    /// <summary>Y 轴正向点动（D4504=2, D4501=1000, D4502=1）</summary>
    public ICommand YPlusCommand { get; }
    /// <summary>Y 轴负向点动（D4504=2, D4501=1000, D4502=2）</summary>
    public ICommand YMinusCommand { get; }
    /// <summary>Z 轴正向点动（D4503=2, D4501=1000, D4502=1）</summary>
    public ICommand ZPlusCommand { get; }
    /// <summary>Z 轴负向点动（D4503=2, D4501=1000, D4502=2）</summary>
    public ICommand ZMinusCommand { get; }

    /// <summary>启动机械手卡片：连接 → 首次读取 → 5 秒轮询。</summary>
    public async Task StartAsync()
    {
        await StopPollingAsync();

        var info = _cache.GetManipulatorInfo(_no);
        Name = info.Name;
        OnPropertyChanged(nameof(Name));

        Console.WriteLine($"[ManipulatorVM] [{Name}] 启动：数据库IP={info.Ip}");
        _service = _cache.GetOrCreateService(_no);

        try
        {
            await _service.ConnectAsync();
            Console.WriteLine($"[ManipulatorVM] [{Name}] 连接成功，开始首次读取...");
            var s = await _service.ReadStatusAsync();
            if (s != null)
            {
                bool x6MagnetOk = false, x7DemagnetOk = false;
                try { x6MagnetOk = await _service.ReadXBitAsync(CraneAddress.D_X6_MagnetizeOk); } catch { }
                try { x7DemagnetOk = await _service.ReadXBitAsync(CraneAddress.D_X7_DemagnetizeOk); } catch { }
                Console.WriteLine($"[ManipulatorVM] [{Name}] 首次读取成功");
                UpdateUi(s, x6MagnetOk, x7DemagnetOk);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ManipulatorVM] [{Name}] 初始连接/读取失败：{ex.Message}");
            SetDisconnectedUi();
        }

        _pollCts = new CancellationTokenSource();
        _pollTask = PollLoopAsync(_pollCts.Token);
        Console.WriteLine($"[ManipulatorVM] [{Name}] 轮询启动，间隔5s");
    }

    /// <summary>重载IP映射前等待旧轮询退出，避免重复读取同一机械手。</summary>
    private async Task StopPollingAsync()
    {
        var cts = _pollCts;
        var task = _pollTask;
        if (cts == null) return;

        cts.Cancel();
        if (task != null)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException)
            {
                throw new TimeoutException($"[ManipulatorVM] [{Name}] 旧轮询10秒内未退出，禁止启动重复轮询");
            }
        }
        _pollCts = null;
        _pollTask = null;
        cts.Dispose();
    }

    /// <summary>
    /// 轮询循环：连接检查 → 读取状态 → 更新 UI。
    /// 连接失败时指数退避（5s → 10s → 20s → 30s max），成功后重置回 5s。
    /// </summary>
    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _service ??= _cache.GetOrCreateService(_no);

                // ── 重连逻辑 ──────────────────────────────────────────
                if (!_service.IsConnected)
                {
                    Console.WriteLine($"[ManipulatorVM] [{Name}] 未连接，尝试重连...");
                    await _service.ConnectAsync(ct);
                    Console.WriteLine($"[ManipulatorVM] [{Name}] ✔ 重连成功");
                    _reconnectDelayMs = ReconnectDelayInitMs;   // 成功后重置为 5s
                }

                // ── 读取状态 ──────────────────────────────────────────
                var s = await _service.ReadStatusAsync(ct);
                if (s != null)
                {
                    bool x6MagnetOk = false, x7DemagnetOk = false;
                    try
                    {
                        x6MagnetOk = await _service.ReadXBitAsync(CraneAddress.D_X6_MagnetizeOk, ct);
                        x7DemagnetOk = await _service.ReadXBitAsync(CraneAddress.D_X7_DemagnetizeOk, ct);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ManipulatorVM] [{Name}] ⚠ 读取X6/X7线圈异常：{ex.Message}");
                    }
                    UpdateUi(s, x6MagnetOk, x7DemagnetOk);
                }
                else
                {
                    Console.WriteLine($"[ManipulatorVM] [{Name}] 读取状态返回 null");
                    SetDisconnectedUi();
                }

                // 正常轮询间隔 5s
                await Task.Delay(ReconnectDelayInitMs, ct);
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine($"[ManipulatorVM] [{Name}] 连接超时：{ex.Message}，{_reconnectDelayMs / 1000}s 后重试");
                SetDisconnectedUi();
                await Task.Delay(_reconnectDelayMs, ct);
                _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, ReconnectDelayMaxMs);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[ManipulatorVM] [{Name}] 通信异常(IO)：{ex.Message}，{_reconnectDelayMs / 1000}s 后重试");
                SetDisconnectedUi();
                await Task.Delay(_reconnectDelayMs, ct);
                _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, ReconnectDelayMaxMs);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[ManipulatorVM] [{Name}] 未知异常：{ex.Message}，{_reconnectDelayMs / 1000}s 后重试");
                SetDisconnectedUi();
                await Task.Delay(_reconnectDelayMs, ct);
                _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, ReconnectDelayMaxMs);
            }
        }
    }

    /// <summary>将状态与X6充磁、X7退磁反馈映射到卡片行（机械手仅展示 Y/Z）。</summary>
    private void UpdateUi(CraneStatus s, bool x6MagnetOk, bool x7DemagnetOk)
    {
        var fault = s.Fault != 0 || s.ServoAlarm != 0 || s.PlcAlarm != 0;
        ConnectedBrush = fault ? Brushes.Red : Brushes.LimeGreen;
        Line1 = fault ? "已连接 | 故障" : "已连接，就绪";
        Line2 = $"任务 #{s.CurrentTaskNo} | 回原点 X={s.XHomeCompleted} Y={s.YHomeCompleted} Z={s.ZHomeCompleted}";
        Line3 = $"回原点状态 X={s.XHomeCompleted} Y={s.YHomeCompleted} Z={s.ZHomeCompleted}";
        Line4 = s.RunConditionMissing != 0 ? $"条件缺失 0x{s.RunConditionMissing:X4}" : "运行条件满足";
        Line5 = $"X6={(x6MagnetOk ? 1 : 0)} X7={(x7DemagnetOk ? 1 : 0)} D5029={s.HasRoller} | Y={s.YPos} Z={s.ZPos}";

        // 坐标只用于页面实时显示和流程安全判断，不再周期写入数据库。
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

    /// <summary>确保机械手已连接，未连接则先 ConnectAsync，返回 <see cref="CraneService"/>。</summary>
    private async Task<CraneService> EnsureConnectedAsync()
    {
        _service ??= _cache.GetOrCreateService(_no);
        if (!_service.IsConnected)
            await _service.ConnectAsync();
        return _service;
    }

    private async Task MoveYAsync(bool positive)
    {
        var svc = await EnsureConnectedAsync();
        const int distance = 1000;
        await svc.MoveYAsync(distance, positive);
        Console.WriteLine($"[ManipulatorVM] [{Name}] 写寄存器成功：Y{(positive ? "+" : "-")} 距离={distance}");
        await LogCurrentPositionAsync(svc, $"Y{(positive ? "+" : "-")}{distance}");
    }

    private async Task MoveZAsync(bool positive)
    {
        var svc = await EnsureConnectedAsync();
        const int distance = 1000;
        await svc.MoveZAsync(distance, positive);
        Console.WriteLine($"[ManipulatorVM] [{Name}] 写寄存器成功：Z{(positive ? "+" : "-")} 距离={distance}");
        await LogCurrentPositionAsync(svc, $"Z{(positive ? "+" : "-")}{distance}");
    }

    private async Task EStopAsync()
    {
        var svc = await EnsureConnectedAsync();
        await svc.EmergencyStopAsync();
        Console.WriteLine($"[ManipulatorVM] [{Name}] 写寄存器成功：急停");
        await LogCurrentPositionAsync(svc, "急停");
    }

    private async Task ServoPowerOnAsync()
    {
        var svc = await EnsureConnectedAsync();
        await svc.ServoPowerOnAsync();
        Console.WriteLine($"[ManipulatorVM] [{Name}] 写寄存器成功：伺服通电");
        await LogCurrentPositionAsync(svc, "伺服通电");
    }

    private async Task ServoPowerOffAsync()
    {
        var svc = await EnsureConnectedAsync();
        await svc.ServoPowerOffAsync();
        Console.WriteLine($"[ManipulatorVM] [{Name}] 写寄存器成功：伺服断电");
        await LogCurrentPositionAsync(svc, "伺服断电");
    }

    private async Task ClearAlarmAsync()
    {
        var svc = await EnsureConnectedAsync();
        await svc.ClearAlarmAsync();
        Console.WriteLine($"[ManipulatorVM] [{Name}] 写寄存器成功：清除报警");
        await LogCurrentPositionAsync(svc, "清除报警");
    }

    /// <summary>延迟 300ms 后回读坐标，输出到控制台日志。</summary>
    private async Task LogCurrentPositionAsync(CraneService svc, string action)
    {
        try
        {
            await Task.Delay(300);
            var s = await svc.ReadStatusAsync();
            if (s == null)
            {
                Console.WriteLine($"[ManipulatorVM] [{Name}] 动作={action} 后读取坐标失败（status=null）");
                return;
            }

            Console.WriteLine($"[ManipulatorVM] [{Name}] 动作={action} 完成，当前坐标 Y={s.YPos}, Z={s.ZPos}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ManipulatorVM] [{Name}] 动作={action} 后读取坐标异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 停止卡片轮询。
    /// 注意: _service 来自 ManipulatorConnectionCache, 是页面和流程引擎共用的连接。
    /// 卡片不拥有这个连接, Dispose 时不能断开, 否则页面切换会把正在生产的引擎连接断掉。
    /// </summary>
    public void Dispose()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollTask = null;
        // 共享连接由 ManipulatorConnectionCache/流程生命周期管理；这里只停轮询, 不 Disconnect。
        Console.WriteLine($"[ManipulatorVM] [{Name}] 已释放资源");
    }
}
