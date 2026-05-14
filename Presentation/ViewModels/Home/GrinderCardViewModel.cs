using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// 研磨机卡片 ViewModel（主页面黄台总览区）。
/// 每张卡片对应一台研磨机，5s 轮询状态并更新 UI。
/// </summary>
public sealed class GrinderCardViewModel : ObservableObject, IDisposable
{
    private readonly string _name;
    private readonly string _ip;
    private readonly PlcGrinderService.GrinderType _type;
    private PlcGrinderService? _svc;
    private CancellationTokenSource? _pollCts;
    private bool _disposed;

    // ── 重连退避 ──────────────────────────────────────────────────
    private int _reconnectDelayMs = 5000;
    private const int DelayInit = 5000;
    private const int DelayMax  = 30000;

    public GrinderCardViewModel(string name, string ip, PlcGrinderService.GrinderType type)
    {
        _name = name;
        _ip   = ip;
        _type = type;
        Title = name;

        EStopCommand    = new AsyncRelayCommand(EStopAsync,    nameof(EStopCommand));
        ClearAlarmCommand = new AsyncRelayCommand(ClearAlarmAsync, nameof(ClearAlarmCommand));
        ReadStatusCommand  = new AsyncRelayCommand(ReadStatusOnceAsync, nameof(ReadStatusCommand));

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

    public ICommand EStopCommand { get; }
    public ICommand ClearAlarmCommand { get; }
    public ICommand ReadStatusCommand { get; }

    // ═══════════════════════════════════════════════════════════════
    //  启动
    // ═══════════════════════════════════════════════════════════════

    public async Task StartAsync()
    {
        Console.WriteLine($"[GrinderCardVM] [{_name}] 启动 IP={_ip}");
        _svc = new PlcGrinderService(_name, _ip, _type);

        try
        {
            await _svc.ConnectAsync();
            Console.WriteLine($"[GrinderCardVM] [{_name}] 连接成功");
            await UpdateUiFromServiceAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GrinderCardVM] [{_name}] 初始连接失败：{ex.Message}");
            SetDisconnected();
        }

        _pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(_pollCts.Token);
        Console.WriteLine($"[GrinderCardVM] [{_name}] 轮询启动，间隔5s");
    }

    // ═══════════════════════════════════════════════════════════════
    //  轮询
    // ═══════════════════════════════════════════════════════════════

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_svc == null || !_svc.IsConnected)
                {
                    Console.WriteLine($"[GrinderCardVM] [{_name}] 未连接，尝试重连...");
                    _svc = new PlcGrinderService(_name, _ip, _type);
                    await _svc.ConnectAsync(ct);
                    Console.WriteLine($"[GrinderCardVM] [{_name}] 重连成功");
                    _reconnectDelayMs = DelayInit;
                }

                await UpdateUiFromServiceAsync();
                await Task.Delay(DelayInit, ct);
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine($"[GrinderCardVM] [{_name}] 连接超时：{ex.Message}");
                SetDisconnected();
                await Task.Delay(_reconnectDelayMs, ct);
                _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, DelayMax);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[GrinderCardVM] [{_name}] 通信异常：{ex.Message}");
                SetDisconnected();
                await Task.Delay(_reconnectDelayMs, ct);
                _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, DelayMax);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  状态更新
    // ═══════════════════════════════════════════════════════════════

    private async Task UpdateUiFromServiceAsync()
    {
        if (_svc == null) return;

        if (_type == PlcGrinderService.GrinderType.TypeA)
        {
            int di = await _svc.ReadDIRawAsync();
            bool fault   = (di & (1 << 0))  != 0;
            bool busy    = (di & (1 << 14)) != 0;
            bool reqData = (di & (1 << 9))  != 0;
            bool stone1  = (di & (1 << 1))  != 0;
            bool stone2  = (di & (1 << 2))  != 0;

            ConnectedBrush = fault ? Brushes.Red : Brushes.LimeGreen;
            Line1Brush = fault ? Brushes.Red : Brushes.Green;
            Line1 = fault ? "已连接 | 故障" : "已连接，就绪";
            Line2 = busy ? "加工中" : (reqData ? "请求数据" : "空闲");
            Line3 = fault ? $"DI=0x{di:X4}" : (stone1 || stone2 ? $"磨石报警 1={stone1} 2={stone2}" : "正常");
            Line4 = "西门子PLC (TypeA)";
            Line4Brush = Brushes.DarkBlue;
            Line5 = $"DI原始=0x{di:X4}";
        }
        else
        {
            int status   = await _svc.GetMachineStatusAsync();
            bool reqData = await _svc.IsRequestDataAsync();
            bool busy    = await _svc.IsMachiningAsync();
            bool door    = await _svc.IsDoorOpenAsync();

            ConnectedBrush = status == 2 ? Brushes.Red : Brushes.LimeGreen;
            Line1Brush = status == 2 ? Brushes.Red : Brushes.Green;
            Line1 = status == 2 ? "已连接 | 报警" : "已连接，就绪";
            Line2 = busy ? "加工中" : (reqData ? "请求数据" : "空闲");
            Line3 = status == 2 ? $"R7308={status}" : (door ? "安全门开" : "就绪");
            Line4 = "新代数控 (TypeB)";
            Line4Brush = Brushes.DarkGreen;
            Line5 = $"R7308={status} 门={(door?"开":"关")}";
        }
    }

    private void SetDisconnected()
    {
        ConnectedBrush = Brushes.Gray;
        Line1 = "未连接";
        Line2 = "—";
        Line3 = "—";
        Line4 = _type == PlcGrinderService.GrinderType.TypeA ? "西门子PLC (TypeA)" : "新代数控 (TypeB)";
        Line5 = "等待重连...";
    }

    // ═══════════════════════════════════════════════════════════════
    //  按钮
    // ═══════════════════════════════════════════════════════════════

    private async Task EStopAsync()
    {
        if (_svc == null || !_svc.IsConnected) return;
        Console.WriteLine($"[GrinderCardVM] [{_name}] ▶ 急停测试（写DO报警位）");
        // 研磨机没有独立急停寄存器，写入报警位测试
    }

    private async Task ClearAlarmAsync()
    {
        if (_svc == null || !_svc.IsConnected) return;
        Console.WriteLine($"[GrinderCardVM] [{_name}] ▶ 清除报警（需确认PLC是否支持）");
        // TypeA: 写40011对应位清报警 / TypeB: 写R7311等
    }

    private async Task ReadStatusOnceAsync()
    {
        if (_svc == null || !_svc.IsConnected)
        {
            Console.WriteLine($"[GrinderCardVM] [{_name}] 未连接，无法读取");
            return;
        }
        Console.WriteLine($"[GrinderCardVM] [{_name}] ▶ 手动读状态");
        await _svc.ReadAllStatusAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _ = _svc?.DisconnectAsync();
        Console.WriteLine($"[GrinderCardVM] [{_name}] 已释放");
    }
}
