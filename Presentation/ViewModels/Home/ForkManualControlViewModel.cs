using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

public sealed record ForkLineOption(int LineNo, string StationCode, string DisplayName);

/// <summary>人工发送单条货叉指令，仅管理手动连接和设备反馈。</summary>
public sealed class ForkManualControlViewModel : ObservableObject, IDisposable
{
    private sealed record Endpoint(string Ip, int Port);
    private sealed record Connection(Endpoint Endpoint, IForkManualDevice Device);
    private readonly Func<string, string, int, IForkManualDevice> _createDevice;
    private readonly Dictionary<string, Endpoint> _endpoints = new();
    private readonly Dictionary<string, Connection> _connections = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ManualCommand[] _commands;
    private int _requestInFlight;
    private int _mappingVersion;
    private bool _disposed;
    private ForkLineOption _selectedLine;
    private ForkStatus? _status;
    private DateTime? _lastRefreshedAt;
    private string _lastOperationResult = "请选择货叉，点击刷新状态或发送动作指令。";

    public ForkManualControlViewModel() : this((name, ip, port) => new ForkManualDevice(name, ip, port)) { }

    public ForkManualControlViewModel(Func<string, string, int, IForkManualDevice> createDevice)
    {
        _createDevice = createDevice ?? throw new ArgumentNullException(nameof(createDevice));
        LineItems = Array.AsReadOnly(new[]
        {
            new ForkLineOption(1, "ST711", "1号线货叉（ST711）"),
            new ForkLineOption(2, "ST712", "2号线货叉（ST712）")
        });
        _selectedLine = LineItems[0];
        _commands = new[]
        {
            new ManualCommand(this, RefreshAsync),
            new ManualCommand(this, GoStandbyAsync),
            new ManualCommand(this, GoFeedToBoringAsync),
            new ManualCommand(this, GoPickFromBoringAsync),
            new ManualCommand(this, GoStandbyToPos3Async)
        };
    }

    public IReadOnlyList<ForkLineOption> LineItems { get; }
    public ForkLineOption SelectedLine
    {
        get => _selectedLine;
        set
        {
            if (!CanOperate || value == null || !LineItems.Contains(value)) return;
            if (!SetField(ref _selectedLine, value)) return;
            ClearFeedback();
            LastOperationResult = $"{value.DisplayName}：请刷新状态或发送动作指令。";
            OnPropertyChanged(nameof(ConnectionStatus));
        }
    }

    public bool IsBusy => Volatile.Read(ref _requestInFlight) != 0;
    public bool CanOperate => !_disposed && !IsBusy;
    public bool? HasPlate => _status?.HasPlate;
    public bool? AtStandbyPos => _status?.AtStandbyPos;
    public bool? AtPos3 => _status?.AtPos3;
    public string HasPlateText => FeedbackText(HasPlate, "有板", "无板");
    public string AtStandbyPosText => FeedbackText(AtStandbyPos, "已到位", "未到位");
    public string AtPos3Text => FeedbackText(AtPos3, "已到位", "未到位");
    public DateTime? LastRefreshedAt => _lastRefreshedAt;
    public string RefreshTimeText => _lastRefreshedAt is { } time ? $"最近刷新：{time:HH:mm:ss}" : "最近刷新：未刷新";
    public string LastOperationResult { get => _lastOperationResult; private set => SetField(ref _lastOperationResult, value); }
    public string ConnectionStatus
    {
        get
        {
            if (!_endpoints.TryGetValue(SelectedLine.StationCode, out var endpoint)) return "未配置IP";
            bool connected = _connections.TryGetValue(SelectedLine.StationCode, out var connection)
                && connection.Endpoint == endpoint && connection.Device.IsConnected;
            return $"{(connected ? "已连接" : "未连接")} · {endpoint.Ip}:{endpoint.Port}";
        }
    }

    public ICommand RefreshStatusCommand => _commands[0];
    public ICommand GoStandbyCommand => _commands[1];
    public ICommand FeedToBoringCommand => _commands[2];
    public ICommand PickFromBoringCommand => _commands[3];
    public ICommand StandbyToPos3Command => _commands[4];

    /// <summary>只保存工位资料，不连接PLC；下次人工请求时才创建或替换客户端。</summary>
    public void LoadMappings(IEnumerable<MachineManagementRowVm> machineRows)
    {
        if (_disposed) return;
        var rows = machineRows.ToArray();
        _endpoints.Clear();
        foreach (var line in LineItems)
        {
            var row = rows.FirstOrDefault(r => string.Equals(r.StationCode?.Trim(), line.StationCode, StringComparison.OrdinalIgnoreCase));
            if (row != null && !string.IsNullOrWhiteSpace(row.Ip))
                _endpoints[line.StationCode] = new Endpoint(row.Ip.Trim(), row.Port > 0 ? row.Port : 9000);
        }
        _mappingVersion++;
        ClearFeedback();
        if (!IsBusy) LastOperationResult = "工位资料已更新，请刷新所选货叉状态。";
        OnPropertyChanged(nameof(ConnectionStatus));
    }

    public Task RefreshAsync() => RunAsync(null, null);
    public Task GoStandbyAsync() => RunAsync("回待机位（M911）", (d, ct) => d.GoStandbyAsync(ct));
    public Task GoFeedToBoringAsync() => RunAsync("双头镗送料并回待机（M912）", (d, ct) => d.GoFeedToBoringAsync(ct));
    public Task GoPickFromBoringAsync() => RunAsync("双头镗取料并到 Pos3（M913）", (d, ct) => d.GoPickFromBoringAsync(ct));
    public Task GoStandbyToPos3Async() => RunAsync("待机位直接到 Pos3（M914）", (d, ct) => d.GoStandbyToPos3Async(ct));

    private async Task RunAsync(string? action, Func<IForkManualDevice, CancellationToken, Task>? send)
    {
        if (_disposed || Interlocked.CompareExchange(ref _requestInFlight, 1, 0) != 0) return;
        var line = SelectedLine;
        int mappingVersion = _mappingVersion;
        bool sent = false;
        bool writeStarted = false;
        string stage = "连接";
        NotifyAvailability();
        ClearFeedback();
        LastOperationResult = $"{line.DisplayName}：{(send == null ? "正在刷新状态…" : $"正在发送{action}…")}";
        try
        {
            if (!_endpoints.TryGetValue(line.StationCode, out var endpoint))
            {
                LastOperationResult = $"{line.DisplayName}：未配置IP，请检查工位资料。";
                return;
            }
            // 一次请求固定设备，不读取自动引擎的连接或状态。
            var device = GetOrCreateDevice(line, endpoint);
            var ct = _lifetime.Token;
            if (!device.IsConnected) await device.ConnectAsync(ct);
            OnPropertyChanged(nameof(ConnectionStatus));
            if (send != null)
            {
                stage = "发送";
                writeStarted = true;
                await send(device, ct);
                sent = true;
                LastOperationResult = $"{line.DisplayName}：{action}命令已发送。";
            }
            stage = "状态刷新";
            var status = await device.ReadAllStatusAsync(ct);
            // 配置被重载时不能把旧设备快照展示成新地址的状态。
            if (mappingVersion == _mappingVersion)
            {
                _status = status;
                _lastRefreshedAt = DateTime.Now;
                NotifyFeedback();
            }
            if (!sent) LastOperationResult = $"{line.DisplayName}：状态已刷新。";
        }
        catch (Exception ex)
        {
            ClearFeedback();
            LastOperationResult = sent
                ? $"{line.DisplayName}：{action}命令已发送；状态刷新失败：{ex.Message}"
                : writeStarted
                    ? $"{line.DisplayName}：{action}发送未确认：{ex.Message}。请核对设备状态，本次未自动重发。"
                    : $"{line.DisplayName}：{stage}失败：{ex.Message}";
        }
        finally
        {
            if (mappingVersion != _mappingVersion)
            {
                ClearFeedback();
                LastOperationResult += " 工位资料已更新，请重新刷新状态。";
            }
            Interlocked.Exchange(ref _requestInFlight, 0);
            NotifyAvailability();
            OnPropertyChanged(nameof(ConnectionStatus));
        }
    }

    private IForkManualDevice GetOrCreateDevice(ForkLineOption line, Endpoint endpoint)
    {
        if (_connections.TryGetValue(line.StationCode, out var previous))
        {
            if (previous.Endpoint == endpoint) return previous.Device;
            previous.Device.Dispose();
            _connections.Remove(line.StationCode);
        }
        var device = _createDevice($"手动-{line.DisplayName}", endpoint.Ip, endpoint.Port);
        _connections[line.StationCode] = new Connection(endpoint, device);
        return device;
    }

    private static string FeedbackText(bool? value, string yes, string no) => value.HasValue ? (value.Value ? yes : no) : "未知";
    private void ClearFeedback()
    {
        _status = null;
        _lastRefreshedAt = null;
        NotifyFeedback();
    }
    private void NotifyFeedback()
    {
        foreach (var property in new[] { nameof(HasPlate), nameof(AtStandbyPos), nameof(AtPos3), nameof(HasPlateText), nameof(AtStandbyPosText), nameof(AtPos3Text), nameof(LastRefreshedAt), nameof(RefreshTimeText) })
            OnPropertyChanged(property);
    }
    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanOperate));
        foreach (var command in _commands) command.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        foreach (var connection in _connections.Values)
        {
            try { connection.Device.Dispose(); }
            catch (Exception ex) { Console.WriteLine($"[手动货叉] 释放连接失败：{ex.Message}"); }
        }
        _connections.Clear();
        _lifetime.Dispose();
        ClearFeedback();
        NotifyAvailability();
        OnPropertyChanged(nameof(ConnectionStatus));
    }

    private sealed class ManualCommand(ForkManualControlViewModel owner, Func<Task> execute) : ICommand
    {
        public bool CanExecute(object? parameter) => owner.CanOperate;
        public async void Execute(object? parameter)
        {
            if (CanExecute(parameter)) await execute();
        }
        public event EventHandler? CanExecuteChanged;
        public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
