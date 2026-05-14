using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using AutomaticOnlineHostComputer.Communication.Clients;
using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// 全厂状态总览中单个站位的卡片 ViewModel。
/// 封装站位的连接状态、设备状态、IP 显示，支持 5 秒轮询。
/// </summary>
public sealed class StationCardViewModel : ObservableObject, IDisposable
{
    private readonly string _stationCode;
    private readonly string _stationName;
    private readonly string _ip;
    private readonly int _port;
    private readonly string _protocol;

    private ModbusTcpClient? _modbusClient;
    private CancellationTokenSource? _pollCts;
    private bool _disposed;

    // ═══════════════════════════════════════════════════════════════
    //  构造
    // ═══════════════════════════════════════════════════════════════

    /// <param name="stationCode">站号（如 ST401）</param>
    /// <param name="stationName">站名（如"一号双头镗"）</param>
    /// <param name="ip">IP 地址，空字符串或"未配置IP"表示暂无连接</param>
    /// <param name="port">端口</param>
    /// <param name="protocol">协议名（ModbusTCP/FOCAS/Syntec/三菱MC/文件握手）</param>
    public StationCardViewModel(string stationCode, string stationName, string ip, int port, string protocol)
    {
        _stationCode = stationCode;
        _stationName = stationName;
        _ip = ip;
        _port = port;
        _protocol = protocol;

        Title = stationName;
        UpdateIpDisplay();
    }

    // ═══════════════════════════════════════════════════════════════
    //  绑定属性
    // ═══════════════════════════════════════════════════════════════

    /// <summary>卡片标题（站名）</summary>
    private string _title = string.Empty;
    public string Title { get => _title; private set => SetField(ref _title, value); }

    /// <summary>第 1 行：连接/设备状态文本</summary>
    private string _status1 = "等待配置";
    public string Status1 { get => _status1; set => SetField(ref _status1, value); }

    /// <summary>第 2 行：设备状态补充信息</summary>
    private string _status2 = "—";
    public string Status2 { get => _status2; set => SetField(ref _status2, value); }

    /// <summary>IP 地址显示文本</summary>
    private string _ipText = "未配置IP";
    public string IpText { get => _ipText; set => SetField(ref _ipText, value); }

    /// <summary>连接指示灯颜色</summary>
    private Brush _connectedBrush = Brushes.Gray;
    public Brush ConnectedBrush { get => _connectedBrush; set => SetField(ref _connectedBrush, value); }

    /// <summary>Status1 文字颜色</summary>
    private Brush _status1Brush = Brushes.Black;
    public Brush Status1Brush { get => _status1Brush; set => SetField(ref _status1Brush, value); }

    /// <summary>Status2 文字颜色</summary>
    private Brush _status2Brush = Brushes.Black;
    public Brush Status2Brush { get => _status2Brush; set => SetField(ref _status2Brush, value); }

    // ═══════════════════════════════════════════════════════════════
    //  公共方法
    // ═══════════════════════════════════════════════════════════════

    /// <summary>是否有有效 IP（可以尝试连接）</summary>
    public bool HasIp => !string.IsNullOrWhiteSpace(_ip) && _ip != "未配置IP";

    /// <summary>
    /// 启动轮询：Modbus TCP 设备尝试连接并 5s 轮询状态。
    /// 无 IP 则显示"等待配置"。
    /// </summary>
    public async Task StartPollingAsync()
    {
        if (!HasIp)
        {
            Status1 = "等待IP";
            Status2 = "—";
            ConnectedBrush = Brushes.Gray;
            Status1Brush = Brushes.Gray;
            return;
        }

        try
        {
            _modbusClient = new ModbusTcpClient(_ip, _port);
            Console.WriteLine($"[StationCard] [{_stationCode}] {_stationName} 连接中 IP={_ip}:{_port}...");
            await _modbusClient.ConnectAsync();
            Console.WriteLine($"[StationCard] [{_stationCode}] {_stationName} 连接成功");

            ConnectedBrush = Brushes.LimeGreen;
            Status1 = "已连接";
            Status1Brush = Brushes.Green;
            Status2 = $"{_protocol} :{_port}";

            _pollCts = new CancellationTokenSource();
            _ = PollLoopAsync(_pollCts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StationCard] [{_stationCode}] {_stationName} 连接失败：{ex.Message}");
            Status1 = "连接失败";
            Status2 = "检查网络/IP";
            ConnectedBrush = Brushes.Red;
            Status1Brush = Brushes.Red;

            // 即使首次连接失败也启动轮询（带退避重连）
            _pollCts = new CancellationTokenSource();
            _ = PollLoopAsync(_pollCts.Token);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  轮询循环
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 5 秒间隔轮询：尝试重连 → 读取设备状态 → 更新 UI。
    /// 连接失败时指数退避（5s→10s→20s→30s max），成功后重置为 5s。
    /// </summary>
    private async Task PollLoopAsync(CancellationToken ct)
    {
        int reconnectDelay = 5000;
        const int delayInit = 5000;
        const int delayMax = 30000;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_modbusClient == null || !_modbusClient.IsConnected)
                {
                    if (!HasIp) { await Task.Delay(5000, ct); continue; }

                    Console.WriteLine($"[StationCard] [{_stationCode}] 未连接，尝试重连...");
                    // 断开旧连接释放 TCP 资源，避免泄漏累积耗尽 PLC 连接数
                    if (_modbusClient != null)
                    {
                        try { await _modbusClient.DisposeAsync(); }
                        catch (Exception ex) { Console.WriteLine($"[StationCard] [{_stationCode}] 释放旧连接异常：{ex.Message}"); }
                    }
                    _modbusClient = new ModbusTcpClient(_ip, _port);
                    await _modbusClient.ConnectAsync(ct);
                    Console.WriteLine($"[StationCard] [{_stationCode}] 重连成功");
                    reconnectDelay = delayInit;
                }

                // 读取设备状态：D5006（忙闲）~D5010（条件缺失），共 5 个寄存器
                // IntValues: [0]=D5006(Busy),[1]=D5007(步骤),[2]=D5008(模式),[3]=D5009(故障),[4]=D5010(条件)
                var result = await _modbusClient.ReadAsync(5006, 5, ct);
                if (result != null && result.IntValues.Length >= 5)
                {
                    int busy  = result.IntValues[0];  // D5006: 忙碌=1, 空闲=0
                    int fault = result.IntValues[3];  // D5009: 故障=1, 正常=0
                    int cond  = result.IntValues[4];  // D5010: 运行条件缺失

                    bool hasFault = fault != 0;
                    ConnectedBrush = hasFault ? Brushes.Red : Brushes.LimeGreen;
                    Status1Brush = hasFault ? Brushes.Red : Brushes.Green;
                    Status1 = hasFault ? "故障" : (busy == 1 ? "运行中" : "空闲");
                    Status2 = hasFault ? $"报警 D5009={fault}" : (busy == 1 ? "加工中" : $"条件={cond}");
                }

                await Task.Delay(delayInit, ct);
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine($"[StationCard] [{_stationCode}] 连接超时：{ex.Message}");
                SetDisconnected();
                await Task.Delay(reconnectDelay, ct);
                reconnectDelay = Math.Min(reconnectDelay * 2, delayMax);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[StationCard] [{_stationCode}] 通信异常：{ex.Message}");
                SetDisconnected();
                await Task.Delay(reconnectDelay, ct);
                reconnectDelay = Math.Min(reconnectDelay * 2, delayMax);
            }
        }
    }

    private void SetDisconnected()
    {
        ConnectedBrush = Brushes.Gray;
        Status1 = "断开";
        Status1Brush = Brushes.Gray;
        Status2 = "等待重连...";
    }

    private void UpdateIpDisplay()
    {
        IpText = HasIp ? $"{_ip}:{_port}" : "未配置IP";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        // DisposeAsync 断开 TCP + 释放 SemaphoreSlim 内核对象
        if (_modbusClient != null)
        {
            _ = _modbusClient.DisposeAsync();
            Console.WriteLine($"[StationCard] [{_stationCode}] 已释放 TCP + 锁");
        }
    }
}
