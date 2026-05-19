using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// 下拉框中的设备选项（天车或机械手）。
/// </summary>
public sealed class ManualDeviceItem
{
    /// <summary>设备类型。</summary>
    public ManualDeviceType DeviceType { get; init; }
    /// <summary>编号（天车 1~5，机械手 1~3）。</summary>
    public int Index { get; init; }
    /// <summary>下拉框显示文本，如 "天车1 - 1号线天车前"。NotifyMappingsUpdated 时动态刷新。</summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>备用的简短名称（如 "1号线天车前"），日志输出用。</summary>
    public string ShortName { get; init; } = string.Empty;

    public override string ToString() => DisplayName;
}

/// <summary>设备类型。</summary>
public enum ManualDeviceType
{
    /// <summary>天车（5 台：1号线前/后、2号线前/后、研磨天车）</summary>
    Crane,
    /// <summary>小机械手（3 台：机械手1/2/3）</summary>
    Manipulator
}

/// <summary>
/// 主页面手动控制面板 ViewModel。
/// <para>
/// 下拉框列出 5 台天车 + 3 台小机械手，可对选中设备执行点动、回原点、急停等操作。
/// </para>
/// <para>
/// 机械手与天车共用同一套 Modbus 寄存器（汇川 PLC），因此底层统一走 <see cref="CraneService"/>。
/// </para>
/// <para>
/// 机械手选中时自动禁用 X 轴（点动/回原点）、充/退磁、接液盘开/关按钮（机械手无这些硬件功能）。
/// </para>
///
///
/// 
/// </summary>
public sealed class CraneManualControlViewModel : ObservableObject
{
    private readonly CraneConnectionCache _craneCache;
    private readonly ManipulatorConnectionCache _manipulatorCache;
    private readonly PositionUpdateService? _posSvc;

    // ═══════════════════════════════════════════════════════════════
    //  下拉框数据
    // ═══════════════════════════════════════════════════════════════

    /// <summary>下拉框数据源（8 项：5 天车 + 3 机械手），名称在装载缓存后动态填充。</summary>
    public List<ManualDeviceItem> DeviceItems { get; }

    private ManualDeviceItem? _selectedDevice;
    /// <summary>当前选中的设备，双向绑定到 ComboBox.SelectedItem。
    /// 切换设备时自动读取当前位置并填入目标坐标输入框。</summary>
    public ManualDeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetField(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(SelectedDeviceDisplay));
                OnPropertyChanged(nameof(IsCraneSelected));
                Console.WriteLine($"[CraneManualVM] 已切换设备 -> {SelectedDeviceDisplay}");
                // 切换设备后自动读取当前位置填入目标输入框
                _ = RefreshTargetsFromCurrentAsync();
            }
        }
    }

    /// <summary>选中设备的描述文本（含 IP），显示在下拉框右侧。</summary>
    public string SelectedDeviceDisplay
    {
        get
        {
            if (_selectedDevice == null)
                return "未选择设备";

            try
            {
                var (name, ip) = _selectedDevice.DeviceType == ManualDeviceType.Manipulator
                    ? (_selectedDevice.ShortName, _manipulatorCache.GetManipulatorInfo(_selectedDevice.Index).Ip)
                    : (_selectedDevice.ShortName, _craneCache.GetCraneInfo(_selectedDevice.Index).Ip);
                return $"{name}  {ip}";
            }
            catch
            {
                return $"{_selectedDevice.ShortName} (未配置IP)";
            }
        }
    }

    /// <summary>
    /// 当前是否选中天车（非机械手）。
    /// 绑定到 X 轴/充退磁/接液盘相关按钮的 IsEnabled，机械手时灰掉。
    /// </summary>
    public bool IsCraneSelected => _selectedDevice?.DeviceType == ManualDeviceType.Crane;

    // ═══════════════════════════════════════════════════════════════
    //  点动距离
    // ═══════════════════════════════════════════════════════════════

    /// <summary>单次点动距离（mm），默认 1000，用户可修改。</summary>
    private int _stepDistance = 1000;
    public int StepDistance
    {
        get => _stepDistance;
        set => SetField(ref _stepDistance, value);
    }

    // ═══════════════════════════════════════════════════════════════
    //  相对运动 速度设置（每个轴独立）
    // ═══════════════════════════════════════════════════════════════
    public int RelSpeedX { get => _relSpeedX; set => SetField(ref _relSpeedX, value); }
    public int RelAccelX { get => _relAccelX; set => SetField(ref _relAccelX, value); }
    public int RelDecelX { get => _relDecelX; set => SetField(ref _relDecelX, value); }
    public int RelSpeedY { get => _relSpeedY; set => SetField(ref _relSpeedY, value); }
    public int RelAccelY { get => _relAccelY; set => SetField(ref _relAccelY, value); }
    public int RelDecelY { get => _relDecelY; set => SetField(ref _relDecelY, value); }
    public int RelSpeedZ { get => _relSpeedZ; set => SetField(ref _relSpeedZ, value); }
    public int RelAccelZ { get => _relAccelZ; set => SetField(ref _relAccelZ, value); }
    public int RelDecelZ { get => _relDecelZ; set => SetField(ref _relDecelZ, value); }
    private int _relSpeedX=300, _relAccelX=150, _relDecelX=150;
    private int _relSpeedY=300, _relAccelY=150, _relDecelY=150;
    private int _relSpeedZ=200, _relAccelZ=100, _relDecelZ=100;

    // ═══════════════════════════════════════════════════════════════
    //  绝对位移 速度设置（每个轴独立）
    // ═══════════════════════════════════════════════════════════════
    public int AbsSpeedX { get => _absSpeedX; set => SetField(ref _absSpeedX, value); }
    public int AbsAccelX { get => _absAccelX; set => SetField(ref _absAccelX, value); }
    public int AbsDecelX { get => _absDecelX; set => SetField(ref _absDecelX, value); }
    public int AbsSpeedY { get => _absSpeedY; set => SetField(ref _absSpeedY, value); }
    public int AbsAccelY { get => _absAccelY; set => SetField(ref _absAccelY, value); }
    public int AbsDecelY { get => _absDecelY; set => SetField(ref _absDecelY, value); }
    public int AbsSpeedZ { get => _absSpeedZ; set => SetField(ref _absSpeedZ, value); }
    public int AbsAccelZ { get => _absAccelZ; set => SetField(ref _absAccelZ, value); }
    public int AbsDecelZ { get => _absDecelZ; set => SetField(ref _absDecelZ, value); }
    private int _absSpeedX=150, _absAccelX=100, _absDecelX=50;
    private int _absSpeedY=50, _absAccelY=30, _absDecelY=20;
    private int _absSpeedZ=50, _absAccelZ=30, _absDecelZ=20;

    /// <summary>绝对位移 X 目标坐标（mm），-1 表示跳过 X 轴</summary>
    private int _absXTarget;
    public int AbsXTarget { get => _absXTarget; set => SetField(ref _absXTarget, value); }

    /// <summary>绝对位移 Y 目标坐标（mm）</summary>
    private int _absYTarget;
    public int AbsYTarget { get => _absYTarget; set => SetField(ref _absYTarget, value); }

    /// <summary>绝对位移 Z 目标坐标（mm）</summary>
    private int _absZTarget;
    public int AbsZTarget { get => _absZTarget; set => SetField(ref _absZTarget, value); }

    // ═══════════════════════════════════════════════════════════════
    //  命令属性（绑定到 XAML 按钮 Command）
    //  每个命令通过 AsyncRelayCommand 延迟执行，确保不阻塞 UI 线程
    // ═══════════════════════════════════════════════════════════════

    /// <summary>X 轴正向点动（D4505=2, D4501=距离, D4502=1）—— 仅天车有效</summary>
    public ICommand XPlusCommand { get; }
    /// <summary>X 轴负向点动（D4505=2, D4501=距离, D4502=2）—— 仅天车有效</summary>
    public ICommand XMinusCommand { get; }
    /// <summary>Y 轴正向点动（D4504=2）</summary>
    public ICommand YPlusCommand { get; }
    /// <summary>Y 轴负向点动（D4504=2）</summary>
    public ICommand YMinusCommand { get; }
    /// <summary>Z 轴正向点动（D4503=2）</summary>
    public ICommand ZPlusCommand { get; }
    /// <summary>Z 轴负向点动（D4503=2）</summary>
    public ICommand ZMinusCommand { get; }

    /// <summary>减速停止（D4517=1）</summary>
    public ICommand StopCommand { get; }
    /// <summary>急停（D4518=1，不依赖手动模式，紧急直接写入）</summary>
    public ICommand EStopCommand { get; }
    /// <summary>伺服通电（D4516=1，需手动模式）</summary>
    public ICommand ServoPowerOnCommand { get; }
    /// <summary>伺服断电（D4515=1，需手动模式）</summary>
    public ICommand ServoPowerOffCommand { get; }
    /// <summary>清除报警（D4514=1，需手动模式）</summary>
    public ICommand ClearFaultCommand { get; }
    /// <summary>充磁/磁铁吸合（D4510=1，需手动模式）—— 仅天车有效</summary>
    public ICommand MagnetOnCommand { get; }
    /// <summary>退磁/磁铁释放（D4511=1，需手动模式）—— 仅天车有效</summary>
    public ICommand MagnetOffCommand { get; }
    /// <summary>接液盘打开（D4512=1，需手动模式）—— 仅天车有效</summary>
    public ICommand DrainOpenCommand { get; }
    /// <summary>接液盘关闭（D4513=1，需手动模式）—— 仅天车有效</summary>
    public ICommand DrainCloseCommand { get; }
    /// <summary>X 轴回原点（D4508=2，需手动模式）—— 仅天车有效</summary>
    public ICommand HomeXCommand { get; }
    /// <summary>Y 轴回原点（D4507=2，需手动模式）</summary>
    public ICommand HomeYCommand { get; }
    /// <summary>Z 轴回原点（D4506=2，需手动模式）</summary>
    public ICommand HomeZCommand { get; }

    /// <summary>应用相对速度设置（D2504~D2506 / D2513~D2515 / D2522~D2524），三轴统一值</summary>
    public ICommand SetRelSpeedCommand { get; }

    /// <summary>应用绝对速度设置（D2501~D2503 / D2510~D2512 / D2519~D2521），三轴统一值</summary>
    public ICommand SetAbsSpeedCommand { get; }

    /// <summary>绝对移动：写目标坐标 → 触发 → 轮询到位 → 复位</summary>
    public ICommand MoveAbsoluteCommand { get; }

    /// <summary>读取当前位置填入目标输入框</summary>
    public ICommand RefreshTargetCommand { get; }

    // ═══════════════════════════════════════════════════════════════
    //  构造
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 构造手动控制 VM。
    /// 参数缓存需先调用 <c>LoadFromMachineRows()</c> 装载 IP 映射。
    /// </summary>
    public CraneManualControlViewModel(CraneConnectionCache craneCache, ManipulatorConnectionCache manipulatorCache,
        PositionUpdateService? posSvc = null)
    {
        _craneCache = craneCache;
        _manipulatorCache = manipulatorCache;
        _posSvc = posSvc;

        // ── 构建下拉框 8 项（名称占位，LoadAsync 后刷新） ──────────
        DeviceItems = new List<ManualDeviceItem>
        {
            new() { DeviceType = ManualDeviceType.Crane,       Index = 1, DisplayName = "天车1", ShortName = "1号线天车前" },
            new() { DeviceType = ManualDeviceType.Crane,       Index = 2, DisplayName = "天车2", ShortName = "1号线天车后" },
            new() { DeviceType = ManualDeviceType.Crane,       Index = 3, DisplayName = "天车3", ShortName = "2号线天车前" },
            new() { DeviceType = ManualDeviceType.Crane,       Index = 4, DisplayName = "天车4", ShortName = "2号线天车后" },
            new() { DeviceType = ManualDeviceType.Crane,       Index = 5, DisplayName = "天车5", ShortName = "研磨机天车" },
            new() { DeviceType = ManualDeviceType.Manipulator, Index = 1, DisplayName = "机械手1",  ShortName = "机械手1" },
            new() { DeviceType = ManualDeviceType.Manipulator, Index = 2, DisplayName = "机械手2",  ShortName = "机械手2" },
            new() { DeviceType = ManualDeviceType.Manipulator, Index = 3, DisplayName = "机械手3",  ShortName = "机械手3" },
        };
        SelectedDevice = DeviceItems[0]; // 默认选中天车1

        // ── 绑定命令 ────────────────────────────────────────────────
        XPlusCommand    = new AsyncRelayCommand(() => MoveAxisAsync('X', true),  nameof(XPlusCommand));
        XMinusCommand   = new AsyncRelayCommand(() => MoveAxisAsync('X', false), nameof(XMinusCommand));
        YPlusCommand    = new AsyncRelayCommand(() => MoveAxisAsync('Y', true),  nameof(YPlusCommand));
        YMinusCommand   = new AsyncRelayCommand(() => MoveAxisAsync('Y', false), nameof(YMinusCommand));
        ZPlusCommand    = new AsyncRelayCommand(() => MoveAxisAsync('Z', true),  nameof(ZPlusCommand));
        ZMinusCommand   = new AsyncRelayCommand(() => MoveAxisAsync('Z', false), nameof(ZMinusCommand));

        StopCommand         = new AsyncRelayCommand(StopAsync,         nameof(StopCommand));
        EStopCommand        = new AsyncRelayCommand(EStopAsync,        nameof(EStopCommand));
        ServoPowerOnCommand = new AsyncRelayCommand(ServoPowerOnAsync, nameof(ServoPowerOnCommand));
        ServoPowerOffCommand= new AsyncRelayCommand(ServoPowerOffAsync,nameof(ServoPowerOffCommand));
        ClearFaultCommand   = new AsyncRelayCommand(ClearFaultAsync,   nameof(ClearFaultCommand));
        MagnetOnCommand     = new AsyncRelayCommand(MagnetOnAsync,     nameof(MagnetOnCommand));
        MagnetOffCommand    = new AsyncRelayCommand(MagnetOffAsync,    nameof(MagnetOffCommand));
        DrainOpenCommand    = new AsyncRelayCommand(DrainOpenAsync,    nameof(DrainOpenCommand));
        DrainCloseCommand   = new AsyncRelayCommand(DrainCloseAsync,   nameof(DrainCloseCommand));
        HomeXCommand        = new AsyncRelayCommand(HomeXAsync,        nameof(HomeXCommand));
        HomeYCommand        = new AsyncRelayCommand(HomeYAsync,        nameof(HomeYCommand));
        HomeZCommand        = new AsyncRelayCommand(HomeZAsync,        nameof(HomeZCommand));
        SetRelSpeedCommand  = new AsyncRelayCommand(SetRelSpeedAsync,  nameof(SetRelSpeedCommand));
        SetAbsSpeedCommand  = new AsyncRelayCommand(SetAbsSpeedAsync,  nameof(SetAbsSpeedCommand));
        MoveAbsoluteCommand = new AsyncRelayCommand(MoveAbsoluteAsync, nameof(MoveAbsoluteCommand));
        RefreshTargetCommand = new AsyncRelayCommand(RefreshTargetsFromCurrentAsync, nameof(RefreshTargetCommand));

        Console.WriteLine("[CraneManualVM] 手动控制VM已创建（8设备：5天车+3机械手）。");
    }

    // ═══════════════════════════════════════════════════════════════
    //  公共方法
    // ═══════════════════════════════════════════════════════════════

    /// <summary>缓存 IP 映射更新后刷新下拉框名称和选中设备显示文本。</summary>
    public void NotifyMappingsUpdated()
    {
        // 用缓存中的真实名刷新 8 项的 DisplayName
        foreach (var item in DeviceItems)
        {
            try
            {
                var name = item.DeviceType == ManualDeviceType.Manipulator
                    ? _manipulatorCache.GetManipulatorInfo(item.Index).Name
                    : _craneCache.GetCraneInfo(item.Index).Name;
                item.DisplayName = item.DeviceType == ManualDeviceType.Manipulator
                    ? $"{item.ShortName} ({name})"
                    : $"天车{item.Index} - {name}";
            }
            catch { /* 缓存未装载则保持占位名 */ }
        }

        OnPropertyChanged(nameof(SelectedDeviceDisplay));
        OnPropertyChanged(nameof(DeviceItems));
        Console.WriteLine("[CraneManualVM] 已收到缓存映射更新通知，下拉框名称已刷新。");
    }

    // ═══════════════════════════════════════════════════════════════
    //  连接管理
    // ═══════════════════════════════════════════════════════════════

    /// <summary>手动按钮超时（秒）。后台轮询可能占着 Modbus 锁，手动命令设短超时避免 UI 卡死。</summary>
    private static readonly TimeSpan ManualCommandTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 确保选中设备已连接（根据类型走天车或机械手缓存），返回其 <see cref="CraneService"/> 实例。
    /// 重复调用复用已有连接。
    /// </summary>
    private async Task<CraneService> EnsureConnectedServiceAsync(CancellationToken ct = default)
    {
        if (_selectedDevice == null)
            throw new InvalidOperationException("未选择设备");

        CraneService service;
        string name, ip;

        if (_selectedDevice.DeviceType == ManualDeviceType.Manipulator)
        {
            var info = _manipulatorCache.GetManipulatorInfo(_selectedDevice.Index);
            name = info.Name;
            ip = info.Ip;
            service = _manipulatorCache.GetOrCreateService(_selectedDevice.Index);
            Console.WriteLine($"[CraneManualVM] [机械手] {name} 使用缓存IP={ip}");
        }
        else
        {
            var info = _craneCache.GetCraneInfo(_selectedDevice.Index);
            name = info.Name;
            ip = info.Ip;
            service = _craneCache.GetOrCreateService(_selectedDevice.Index);
            Console.WriteLine($"[CraneManualVM] [天车] {name} 使用缓存IP={ip}");
        }

        if (!service.IsConnected)
        {
            Console.WriteLine($"[CraneManualVM] [{name}] 未连接，开始连接...");
            await service.ConnectAsync(ct);
            Console.WriteLine($"[CraneManualVM] [{name}] 连接成功");
        }
        else
        {
            Console.WriteLine($"[CraneManualVM] [{name}] 已连接，复用会话");
        }

        return service;
    }

    /// <summary>获取当前选中设备的名称（日志输出用）。</summary>
    private string CurrentDeviceName => _selectedDevice?.ShortName ?? "未知设备";

    // ═══════════════════════════════════════════════════════════════
    //  点动
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 通用点动：切换手动模式 → 设置距离/方向 → 触发轴相对运动 → 等 300ms 后回读坐标。
    /// 机械手调用 X 轴会在命令层被 IsCraneSelected 阻止，此处做最后兜底。
    /// </summary>
    private async Task MoveAxisAsync(char axis, bool positive)
    {
        if (_selectedDevice?.DeviceType != ManualDeviceType.Crane && axis == 'X')
        {
            Console.WriteLine($"[CraneManualVM] 机械手不支持 X 轴运动，已忽略。");
            return;
        }

        if (StepDistance <= 0)
        {
            Console.WriteLine($"[CraneManualVM] 步长无效：{StepDistance}");
            return;
        }

        var dirLabel = positive ? "正向(+" : "负向(-";
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【{axis}轴{dirLabel})】 步长={StepDistance}mm");

        using var cts = new CancellationTokenSource(ManualCommandTimeout);
        var service = await EnsureConnectedServiceAsync(cts.Token);
        var name = CurrentDeviceName;

        switch (axis)
        {
            case 'X': await service.MoveXAsync(StepDistance, positive, cts.Token); break;
            case 'Y': await service.MoveYAsync(StepDistance, positive, cts.Token); break;
            case 'Z': await service.MoveZAsync(StepDistance, positive, cts.Token); break;
        }

        Console.WriteLine($"[CraneManualVM] [{name}] ✔ 写入寄存器成功：{axis}轴{dirLabel}) D450{(axis == 'Z' ? "3" : (axis == 'Y' ? "4" : "5"))}=2 步长={StepDistance}");
        await LogCurrentPositionAsync(service, name, $"{axis}{(positive ? "+" : "-")}{StepDistance}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  停止 / 急停
    // ═══════════════════════════════════════════════════════════════

    private async Task StopAsync()
    {
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【减速停止】");
        using var cts = new CancellationTokenSource(ManualCommandTimeout);
        var service = await EnsureConnectedServiceAsync(cts.Token);
        await service.SlowStopAsync(cts.Token);
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ✔ 写入寄存器成功：减速停止 D4517=2→0");
    }

    private async Task EStopAsync()
    {
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【急停】");
        using var cts = new CancellationTokenSource(ManualCommandTimeout);
        var service = await EnsureConnectedServiceAsync(cts.Token);
        await service.EmergencyStopAsync(cts.Token);
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ✔ 写入寄存器成功：急停 D4518=2→0");
    }

    // ═══════════════════════════════════════════════════════════════
    //  伺服通/断电
    // ═══════════════════════════════════════════════════════════════

    private async Task ServoPowerOnAsync()
    {
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【伺服通电】");
        using var cts = new CancellationTokenSource(ManualCommandTimeout);
        var service = await EnsureConnectedServiceAsync(cts.Token);
        await service.ServoPowerOnAsync(cts.Token);
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ✔ 写入寄存器成功：伺服通电 D4516=2→0");
    }

    private async Task ServoPowerOffAsync()
    {
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【伺服断电】");
        using var cts = new CancellationTokenSource(ManualCommandTimeout);
        var service = await EnsureConnectedServiceAsync(cts.Token);
        await service.ServoPowerOffAsync(cts.Token);
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ✔ 写入寄存器成功：伺服断电 D4515=2→0");
    }

    // ═══════════════════════════════════════════════════════════════
    //  清除报警
    // ═══════════════════════════════════════════════════════════════

    private async Task ClearFaultAsync()
    {
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【清除报警】");
        using var cts = new CancellationTokenSource(ManualCommandTimeout);
        var service = await EnsureConnectedServiceAsync(cts.Token);
        await service.ClearAlarmAsync(cts.Token);
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ✔ 写入寄存器成功：清除报警 D4514=2→0");
    }

    // ═══════════════════════════════════════════════════════════════
    //  充磁 / 退磁（仅天车）
    // ═══════════════════════════════════════════════════════════════

    private async Task MagnetOnAsync()
    {
        if (!IsCraneSelected) { Console.WriteLine("[CraneManualVM] 忽略：机械手无充磁功能"); return; }
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【充磁】");
        using var cts = new CancellationTokenSource(ManualCommandTimeout);
        var service = await EnsureConnectedServiceAsync(cts.Token);
        await service.MagnetOnAsync(cts.Token);
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ✔ 写入寄存器成功：充磁 D4510=2→0");
    }

    private async Task MagnetOffAsync()
    {
        if (!IsCraneSelected) { Console.WriteLine("[CraneManualVM] 忽略：机械手无退磁功能"); return; }
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【退磁】");
        using var cts = new CancellationTokenSource(ManualCommandTimeout);
        var service = await EnsureConnectedServiceAsync(cts.Token);
        await service.MagnetOffAsync(cts.Token);
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ✔ 写入寄存器成功：退磁 D4511=2→0");
    }

    // ═══════════════════════════════════════════════════════════════
    //  接液盘（仅天车）
    // ═══════════════════════════════════════════════════════════════

    private async Task DrainOpenAsync()
    {
        if (!IsCraneSelected) { Console.WriteLine("[CraneManualVM] 忽略：机械手无接液盘功能"); return; }
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【接液盘打开】");
        using var cts = new CancellationTokenSource(ManualCommandTimeout);
        var service = await EnsureConnectedServiceAsync(cts.Token);
        await service.DrainOpenAsync(cts.Token);
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ✔ 写入寄存器成功：接液盘打开 D4512=2→0");
    }

    private async Task DrainCloseAsync()
    {
        if (!IsCraneSelected) { Console.WriteLine("[CraneManualVM] 忽略：机械手无接液盘功能"); return; }
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【接液盘关闭】");
        using var cts = new CancellationTokenSource(ManualCommandTimeout);
        var service = await EnsureConnectedServiceAsync(cts.Token);
        await service.DrainCloseAsync(cts.Token);
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ✔ 写入寄存器成功：接液盘关闭 D4513=2→0");
    }

    // ═══════════════════════════════════════════════════════════════
    //  回原点
    // ═══════════════════════════════════════════════════════════════

    private async Task HomeXAsync()
    {
        if (!IsCraneSelected) { Console.WriteLine("[CraneManualVM] 忽略：机械手无 X 轴"); return; }
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【X轴回原点】");
        using var cts = new CancellationTokenSource(ManualCommandTimeout);
        var service = await EnsureConnectedServiceAsync(cts.Token);
        var name = CurrentDeviceName;
        await service.HomeXAsync(cts.Token);
        Console.WriteLine($"[CraneManualVM] [{name}] ✔ 写入寄存器成功：X轴回原点 D4508=2→0");
        await LogCurrentPositionAsync(service, name, "X回原点");
    }

    private async Task HomeYAsync()
    {
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【Y轴回原点】");
        using var cts = new CancellationTokenSource(ManualCommandTimeout);
        var service = await EnsureConnectedServiceAsync(cts.Token);
        var name = CurrentDeviceName;
        await service.HomeYAsync(cts.Token);
        Console.WriteLine($"[CraneManualVM] [{name}] ✔ 写入寄存器成功：Y轴回原点 D4507=2→0");
        await LogCurrentPositionAsync(service, name, "Y回原点");
    }

    private async Task HomeZAsync()
    {
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【Z轴回原点】");
        using var cts = new CancellationTokenSource(ManualCommandTimeout);
        var service = await EnsureConnectedServiceAsync(cts.Token);
        var name = CurrentDeviceName;
        await service.HomeZAsync(cts.Token);
        Console.WriteLine($"[CraneManualVM] [{name}] ✔ 写入寄存器成功：Z轴回原点 D4506=2→0");
        await LogCurrentPositionAsync(service, name, "Z回原点");
    }

    // ═══════════════════════════════════════════════════════════════
    //  辅助
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 读取选中设备的当前坐标，自动填入目标 X/Y/Z 输入框。
    /// 切换设备时自动调用，也可手动触发（加按钮）。
    /// 机械手只填 Y/Z（X 保持 0）。
    /// </summary>
    private async Task RefreshTargetsFromCurrentAsync()
    {
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【读取当前位置填入目标框】");
        try
        {
            using var cts = new CancellationTokenSource(ManualCommandTimeout);
            var service = await EnsureConnectedServiceAsync(cts.Token);
            var status = await service.ReadStatusAsync(cts.Token);
            if (status == null)
            {
                Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] 读取当前位置失败(status=null)，目标框未更新");
                return;
            }

            AbsXTarget = IsCraneSelected ? status.XPos : 0;
            AbsYTarget = status.YPos;
            AbsZTarget = status.ZPos;

            Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] 当前位置已填入目标框 X={AbsXTarget} Y={AbsYTarget} Z={AbsZTarget}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] 读取当前位置异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 应用绝对速度设置：写入选中设备的
    /// D2501~D2503（X）/ D2510~D2512（Y）/ D2519~D2521（Z），
    /// 三轴统一值，一次写入 9 个寄存器。
    /// </summary>
    private async Task SetAbsSpeedAsync()
    {
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【应用绝对速度】 X={AbsSpeedX}/{AbsAccelX}/{AbsDecelX} Y={AbsSpeedY}/{AbsAccelY}/{AbsDecelY} Z={AbsSpeedZ}/{AbsAccelZ}/{AbsDecelZ}");
        using var cts = new CancellationTokenSource(ManualCommandTimeout);
        var service = await EnsureConnectedServiceAsync(cts.Token);
        var name = CurrentDeviceName;
        await service.SetAbsSpeedAsync(AbsSpeedX, AbsAccelX, AbsDecelX, AbsSpeedY, AbsAccelY, AbsDecelY, AbsSpeedZ, AbsAccelZ, AbsDecelZ, cts.Token);
        Console.WriteLine($"[CraneManualVM] [{name}] ✔ 绝对速度写入成功 D2501~D2521");
    }

    /// <summary>
    /// 绝对位移：先应用绝对速度设置 → 写入目标坐标 →
    /// 触发 D4520~D4522=2 → 轮询等待 PLC 到位 → 复位触发=0。
    /// 机械手自动跳过 X 轴（传入 -1）。
    /// 整个过程走 <see cref="CraneService.MoveAbsoluteAsync"/>，异步不阻塞 UI。
    /// </summary>
    private async Task MoveAbsoluteAsync()
    {
        int xTarget = IsCraneSelected ? AbsXTarget : -1;
        int yTarget = AbsYTarget;
        int zTarget = AbsZTarget;
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【绝对移动】 目标 X={xTarget} Y={yTarget} Z={zTarget}");

        using var cts = new CancellationTokenSource(ManualCommandTimeout);
        var service = await EnsureConnectedServiceAsync(cts.Token);
        var name = CurrentDeviceName;

        try
        {
            await service.SetAbsSpeedAsync(AbsSpeedX, AbsAccelX, AbsDecelX, AbsSpeedY, AbsAccelY, AbsDecelY, AbsSpeedZ, AbsAccelZ, AbsDecelZ, cts.Token);
            //绝对位移 service
            await service.MoveAbsoluteAsync(xTarget, yTarget, zTarget, ct: cts.Token);
            Console.WriteLine($"[CraneManualVM] [{name}] ✔ 绝对移动完成");
        }
        catch (TimeoutException ex)
        {
            Console.WriteLine($"[CraneManualVM] [{name}] ✘ 绝对移动超时：{ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CraneManualVM] [{name}] ✘ 绝对移动异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 应用相对速度设置：将速度/加速度/减速度写入选中设备的
    /// D2504~D2506（X）/ D2513~D2515（Y）/ D2522~D2524（Z），
    /// 三轴统一值，一次写入 9 个寄存器。
    /// </summary>
    private async Task SetRelSpeedAsync()
    {
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] ▶ 点击按钮【应用相对速度】 X={RelSpeedX}/{RelAccelX}/{RelDecelX} Y={RelSpeedY}/{RelAccelY}/{RelDecelY} Z={RelSpeedZ}/{RelAccelZ}/{RelDecelZ}");
        using var cts = new CancellationTokenSource(ManualCommandTimeout);
        var service = await EnsureConnectedServiceAsync(cts.Token);
        var name = CurrentDeviceName;
        await service.SetRelSpeedAsync(RelSpeedX, RelAccelX, RelDecelX, RelSpeedY, RelAccelY, RelDecelY, RelSpeedZ, RelAccelZ, RelDecelZ, cts.Token);
        Console.WriteLine($"[CraneManualVM] [{name}] ✔ 相对速度写入成功 D2504~D2524");
    }

    /// <summary>延迟 300ms 后读取设备状态，输出日志 + 异步写库。</summary>
    private async Task LogCurrentPositionAsync(CraneService service, string name, string action)
    {
        try
        {
            await Task.Delay(300);
            var status = await service.ReadStatusAsync();
            if (status == null)
            {
                Console.WriteLine($"[CraneManualVM] [{name}] 动作={action} 后读取坐标失败（status=null）");
                return;
            }

            Console.WriteLine($"[CraneManualVM] [{name}] 动作={action} 完成，当前坐标 X={status.XPos}, Y={status.YPos}, Z={status.ZPos}");

            // 异步更新当前位置到数据库
            if (_posSvc != null)
                _ = _posSvc.UpdateCranePositionAsync(name, status.XPos, status.YPos, status.ZPos);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CraneManualVM] [{name}] 动作={action} 后读取坐标异常：{ex.Message}");
        }
    }
}
