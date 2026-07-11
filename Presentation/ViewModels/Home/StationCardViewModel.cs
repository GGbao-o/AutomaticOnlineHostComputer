using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using AutomaticOnlineHostComputer.Communication.Clients;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// 全厂状态总览中单个站位的卡片 ViewModel。
/// 页面加载时连接一次、读状态、断开。线路启动后由引擎持续刷新。
/// </summary>
public sealed class StationCardViewModel : ObservableObject, IDisposable
{
    private readonly string _stationCode;
    private readonly string _stationName;
    private readonly string _ip;
    private readonly int _port;
    private readonly string _protocol;
    private readonly int _mcRegister; // MC协议设备探测的M寄存器地址,0=默认M800

    private ModbusTcpClient? _modbusClient;
    private bool _disposed;

    // ═══════════════════════════════════════════════════════════════
    //  构造
    // ═══════════════════════════════════════════════════════════════

    public StationCardViewModel(string stationCode, string stationName, string ip, int port, string protocol, int mcRegister = 0)
    {
        _stationCode = stationCode;
        _stationName = stationName;
        _ip = ip;
        _port = port;
        _protocol = protocol;
        _mcRegister = mcRegister;

        Title = stationName;
        UpdateIpDisplay();
    }

    // ═══════════════════════════════════════════════════════════════
    //  绑定属性
    // ═══════════════════════════════════════════════════════════════

    private string _title = string.Empty;
    public string Title { get => _title; private set => SetField(ref _title, value); }

    private string _status1 = "等待探测";
    public string Status1 { get => _status1; set => SetField(ref _status1, value); }

    private string _status2 = "—";
    public string Status2 { get => _status2; set => SetField(ref _status2, value); }

    /// <summary>全流程状态页悬停详情；只由内存快照写入，不参与设备控制。</summary>
    private string _tooltipText = string.Empty;
    public string TooltipText { get => _tooltipText; set => SetField(ref _tooltipText, value); }

    private string _ipText = "未配置IP";
    public string IpText { get => _ipText; set => SetField(ref _ipText, value); }

    private Brush _connectedBrush = Brushes.Gray;
    public Brush ConnectedBrush { get => _connectedBrush; set => SetField(ref _connectedBrush, value); }

    private Brush _status1Brush = Brushes.Black;
    public Brush Status1Brush { get => _status1Brush; set => SetField(ref _status1Brush, value); }

    private Brush _status2Brush = Brushes.Black;
    public Brush Status2Brush { get => _status2Brush; set => SetField(ref _status2Brush, value); }

    // ═══════════════════════════════════════════════════════════════
    //  公共
    // ═══════════════════════════════════════════════════════════════

    public string StationCode => _stationCode;
    public bool HasIp => !string.IsNullOrWhiteSpace(_ip) && _ip != "未配置IP";

    /// <summary>
    /// 页面加载时调用：连一次 → 读 D5006~D5010 → 更新属性 → 断开。
    /// 不重试不轮询。线路启动后由引擎接管刷新。
    /// </summary>
    public async Task StartPollingAsync()
    {
        // FANUC FOCAS 设备由后端流程引擎的独立Worker托管。
        // 页面不能直接FOCAS探测, 否则离线设备会通过Task.Run占用线程池并拖慢UI刷新。
        if (_port == 8193 || _protocol.Contains("FANUC", StringComparison.OrdinalIgnoreCase))
        {
            Status1 = "引擎托管";
            Status2 = HasIp ? $"{_ip}:8193" : "未配置IP";
            ConnectedBrush = Brushes.Gray;
            Status1Brush = Brushes.Gray;
            Console.WriteLine($"[StationCard] [{_stationCode}] {_stationName} FANUC设备({_ip}:8193)，跳过页面直连探测，由后端引擎Worker托管");
            return;
        }

        // MC 协议设备(端口9000) — 不单独探测, 引擎接管(避免多连接抢PLC)
        if (_port == 9000)
        {
            Status1 = "引擎托管";
            Status2 = $"{_ip}:9000";
            ConnectedBrush = Brushes.Gray;
            Status1Brush = Brushes.Gray;
            Console.WriteLine($"[StationCard] [{_stationCode}] {_stationName} MC设备({_ip}:9000)，跳过探测，由引擎托管");
            return;
        }

        if (!HasIp)
        {
            Status1 = "无IP";
            Status2 = "—";
            ConnectedBrush = Brushes.Gray;
            Status1Brush = Brushes.Gray;
            Console.WriteLine($"[StationCard] [{_stationCode}] {_stationName} 无IP，跳过探测");
            return;
        }

        try
        {
            Console.WriteLine($"[StationCard] [{_stationCode}] {_stationName} 一次性探测 {_ip}:{_port}...");
            _modbusClient = new ModbusTcpClient(_ip, _port);
            using var cts = new CancellationTokenSource(4000);
            await _modbusClient.ConnectAsync(cts.Token);
            Console.WriteLine($"[StationCard] [{_stationCode}] {_stationName} TCP连接成功 ✓");

            // 读 D5006~D5010: [Busy][Step][Mode][Fault][Cond]
            var result = await _modbusClient.ReadAsync(5006, 5, cts.Token);
            if (result != null && result.IntValues.Length >= 5)
            {
                int busy  = result.IntValues[0];
                int fault = result.IntValues[3];
                int cond  = result.IntValues[4];

                bool hasFault = fault != 0;
                ConnectedBrush = hasFault ? Brushes.Red : Brushes.LimeGreen;
                Status1Brush = hasFault ? Brushes.Red : Brushes.Green;
                Status1 = hasFault ? "故障" : (busy == 1 ? "运行中" : "空闲");
                Status2 = hasFault ? $"报警 D5009={fault}" : $"就绪 条件={cond}";
                Console.WriteLine($"[StationCard] [{_stationCode}] {_stationName} ✔ 读成功 busy={busy} fault={fault}");
            }
            else
            {
                SetFailed("无响应");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[StationCard] [{_stationCode}] {_stationName} 连接超时(4s)");
            SetFailed("连接超时");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StationCard] [{_stationCode}] {_stationName} 探测失败: {ex.GetType().Name} — {ex.Message}");
            SetFailed(ex.Message.Length > 25 ? ex.Message[..25] : ex.Message);
        }
        finally
        {
            // 断开连接
            if (_modbusClient != null)
            {
                try { await _modbusClient.DisposeAsync(); }
                catch (Exception ex) { Console.WriteLine($"[StationCard] [{_stationCode}] 断开异常: {ex.Message}"); }
                _modbusClient = null;
            }
            Console.WriteLine($"[StationCard] [{_stationCode}] {_stationName} 探测结束，已断开 ✓");
        }
    }

    /// <summary>MC 协议设备一次性探测: 连→读指定M寄存器→断。</summary>
    private async Task ProbeMcAsync()
    {
        if (!HasIp) { SetFailed("无IP"); return; }
        MitsubishiMcClient? mc = null;
        try
        {
            int mAddr = _mcRegister > 0 ? _mcRegister : 800;
            Console.WriteLine($"[StationCard] [{_stationCode}] {_stationName} MC探测 {_ip}:{_port} M{mAddr}...");
            mc = new MitsubishiMcClient(_ip, _port, MitsubishiMcClient.DeviceM,
                frameType: MitsubishiMcClient.McFrameType.A1E) { UseBitReadForM = false };
            using var cts = new CancellationTokenSource(4000);
            await mc.ConnectAsync(cts.Token);
            Console.WriteLine($"[StationCard] [{_stationCode}] {_stationName} MC连接成功 ✓");

            int alignedAddr = mAddr - (mAddr % 16);
            int bitOffset = mAddr - alignedAddr;
            var result = await mc.ReadMAlignedWordAsync(alignedAddr, 1, cts.Token);
            int word = result.IntValues.Length > 0 ? result.IntValues[0] : 0;
            int val = (word & (1 << bitOffset)) != 0 ? 1 : 0;
            bool hasPlate = val != 0;
            ConnectedBrush = Brushes.LimeGreen;
            Status1 = hasPlate ? "有版" : "空闲";
            Status1Brush = hasPlate ? Brushes.Orange : Brushes.Green;
            Status2 = $"M{mAddr}={val}";
            Console.WriteLine($"[StationCard] [{_stationCode}] {_stationName} ✔ MC探测成功 M{mAddr}={val}");
        }
        catch (OperationCanceledException) { SetFailed("MC连接超时"); }
        catch (Exception ex)
        {
            Console.WriteLine($"[StationCard] [{_stationCode}] {_stationName} MC探测失败: {ex.GetType().Name} — {ex.Message}");
            SetFailed(ex.Message.Length > 25 ? ex.Message[..25] : ex.Message);
        }
        finally
        {
            if (mc != null) { try { await mc.DisconnectAsync(); await mc.DisposeAsync(); } catch { } }
        }
    }

    private void SetFailed(string reason)
    {
        ConnectedBrush = Brushes.Red;
        Status1 = "无法连接";
        Status1Brush = Brushes.Red;
        Status2 = reason;
    }

    private void UpdateIpDisplay()
    {
        IpText = HasIp ? $"{_ip}:{_port}" : "未配置IP";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_modbusClient != null)
        {
            _ = _modbusClient.DisposeAsync();
            Console.WriteLine($"[StationCard] [{_stationCode}] 已释放");
        }
    }
}
