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
    //  相对运动 速度设置（D2504~D2506 / D2513~D2515 / D2522~D2524）
    //  手动点动通过 D4501（距离）+ D4502（方向）+ D4503~D4505（触发）
    //  走相对运动模式，PLC 按此处的相对速度值运行
    // ═══════════════════════════════════════════════════════════════

    /// <summary>相对位移速度（默认 500，写 D2504/D2513/D2522）</summary>
    private int _relSpeed = 500;
    public int RelSpeed { get => _relSpeed; set => SetField(ref _relSpeed, value); }

    /// <summary>相对位移加速度（默认 200，写 D2505/D2514/D2523）</summary>
    private int _relAccel = 200;
    public int RelAccel { get => _relAccel; set => SetField(ref _relAccel, value); }

    /// <summary>相对位移减速度（默认 200，写 D2506/D2515/D2524）</summary>
    private int _relDecel = 200;
    public int RelDecel { get => _relDecel; set => SetField(ref _relDecel, value); }

    // ═══════════════════════════════════════════════════════════════
    //  绝对位移 速度/目标设置
    //  绝对移动流程：写绝对速度 → 写目标坐标(DINT) → 触发D4520~D4522=2
    //  → 轮询读取 D5018/D5022/D5025 → 到位后复位触发=0
    // ═══════════════════════════════════════════════════════════════

    /// <summary>绝对位移速度（默认 500，写 D2501/D2510/D2519）</summary>
    private int _absSpeed = 500;
    public int AbsSpeed { get => _absSpeed; set => SetField(ref _absSpeed, value); }

    /// <summary>绝对位移加速度（默认 200，写 D2502/D2511/D2520）</summary>
    private int _absAccel = 200;
    public int AbsAccel { get => _absAccel; set => SetField(ref _absAccel, value); }

    /// <summary>绝对位移减速度（默认 200，写 D2503/D2512/D2521）</summary>
    private int _absDecel = 200;
    public int AbsDecel { get => _absDecel; set => SetField(ref _absDecel, value); }

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

    /// <summary>
    /// 确保选中设备已连接（根据类型走天车或机械手缓存），返回其 <see cref="CraneService"/> 实例。
    /// 重复调用复用已有连接。
    /// </summary>
    private async Task<CraneService> EnsureConnectedServiceAsync()
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
            await service.ConnectAsync();
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

        var service = await EnsureConnectedServiceAsync();
        var name = CurrentDeviceName;
        Console.WriteLine($"[CraneManualVM] [{name}] 写寄存器动作：{axis}{(positive ? "+" : "-")} 步长={StepDistance}");

        switch (axis)
        {
            case 'X': await service.MoveXAsync(StepDistance, positive); break;
            case 'Y': await service.MoveYAsync(StepDistance, positive); break;
            case 'Z': await service.MoveZAsync(StepDistance, positive); break;
        }

        Console.WriteLine($"[CraneManualVM] [{name}] 写寄存器成功：{axis}{(positive ? "+" : "-")}");
        await LogCurrentPositionAsync(service, name, $"{axis}{(positive ? "+" : "-")}{StepDistance}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  停止 / 急停
    // ═══════════════════════════════════════════════════════════════

    private async Task StopAsync()
    {
        var service = await EnsureConnectedServiceAsync();
        await service.SlowStopAsync();
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] 写寄存器成功：减速停止(D4517)");
    }

    private async Task EStopAsync()
    {
        var service = await EnsureConnectedServiceAsync();
        await service.EmergencyStopAsync();
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] 写寄存器成功：急停(D4518)");
    }

    // ═══════════════════════════════════════════════════════════════
    //  伺服通/断电
    // ═══════════════════════════════════════════════════════════════

    private async Task ServoPowerOnAsync()
    {
        var service = await EnsureConnectedServiceAsync();
        await service.ServoPowerOnAsync();
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] 写寄存器成功：伺服通电(D4516)");
    }

    private async Task ServoPowerOffAsync()
    {
        var service = await EnsureConnectedServiceAsync();
        await service.ServoPowerOffAsync();
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] 写寄存器成功：伺服断电(D4515)");
    }

    // ═══════════════════════════════════════════════════════════════
    //  清除报警
    // ═══════════════════════════════════════════════════════════════

    private async Task ClearFaultAsync()
    {
        var service = await EnsureConnectedServiceAsync();
        await service.ClearAlarmAsync();
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] 写寄存器成功：清除报警(D4514)");
    }

    // ═══════════════════════════════════════════════════════════════
    //  充磁 / 退磁（仅天车）
    // ═══════════════════════════════════════════════════════════════

    private async Task MagnetOnAsync()
    {
        if (!IsCraneSelected) { Console.WriteLine("[CraneManualVM] 机械手无充磁功能，已忽略。"); return; }
        var service = await EnsureConnectedServiceAsync();
        await service.MagnetOnAsync();
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] 写寄存器成功：充磁(D4510)");
    }

    private async Task MagnetOffAsync()
    {
        if (!IsCraneSelected) { Console.WriteLine("[CraneManualVM] 机械手无退磁功能，已忽略。"); return; }
        var service = await EnsureConnectedServiceAsync();
        await service.MagnetOffAsync();
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] 写寄存器成功：退磁(D4511)");
    }

    // ═══════════════════════════════════════════════════════════════
    //  接液盘（仅天车）
    // ═══════════════════════════════════════════════════════════════

    private async Task DrainOpenAsync()
    {
        if (!IsCraneSelected) { Console.WriteLine("[CraneManualVM] 机械手无接液盘功能，已忽略。"); return; }
        var service = await EnsureConnectedServiceAsync();
        await service.DrainOpenAsync();
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] 写寄存器成功：接液盘打开(D4512)");
    }

    private async Task DrainCloseAsync()
    {
        if (!IsCraneSelected) { Console.WriteLine("[CraneManualVM] 机械手无接液盘功能，已忽略。"); return; }
        var service = await EnsureConnectedServiceAsync();
        await service.DrainCloseAsync();
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] 写寄存器成功：接液盘关闭(D4513)");
    }

    // ═══════════════════════════════════════════════════════════════
    //  回原点
    // ═══════════════════════════════════════════════════════════════

    private async Task HomeXAsync()
    {
        if (!IsCraneSelected) { Console.WriteLine("[CraneManualVM] 机械手无 X 轴，已忽略。"); return; }
        var service = await EnsureConnectedServiceAsync();
        var name = CurrentDeviceName;
        await service.HomeXAsync();
        Console.WriteLine($"[CraneManualVM] [{name}] 写寄存器成功：X回原点(D4508)");
        await LogCurrentPositionAsync(service, name, "X回原点");
    }

    private async Task HomeYAsync()
    {
        var service = await EnsureConnectedServiceAsync();
        var name = CurrentDeviceName;
        await service.HomeYAsync();
        Console.WriteLine($"[CraneManualVM] [{name}] 写寄存器成功：Y回原点(D4507)");
        await LogCurrentPositionAsync(service, name, "Y回原点");
    }

    private async Task HomeZAsync()
    {
        var service = await EnsureConnectedServiceAsync();
        var name = CurrentDeviceName;
        await service.HomeZAsync();
        Console.WriteLine($"[CraneManualVM] [{name}] 写寄存器成功：Z回原点(D4506)");
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
        try
        {
            var service = await EnsureConnectedServiceAsync();
            var status = await service.ReadStatusAsync();
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
        if (AbsSpeed <= 0 || AbsAccel <= 0 || AbsDecel <= 0)
        {
            Console.WriteLine($"[CraneManualVM] 绝对速度参数无效 speed={AbsSpeed} accel={AbsAccel} decel={AbsDecel}");
            return;
        }

        var service = await EnsureConnectedServiceAsync();
        var name = CurrentDeviceName;
        Console.WriteLine($"[CraneManualVM] [{name}] ▶ 写绝对速度 speed={AbsSpeed} accel={AbsAccel} decel={AbsDecel}");
        await service.SetAbsSpeedAsync(AbsSpeed, AbsAccel, AbsDecel);
        Console.WriteLine($"[CraneManualVM] [{name}] ✔ 绝对速度设置完成");
    }

    /// <summary>
    /// 绝对位移：先应用绝对速度设置 → 写入目标坐标 →
    /// 触发 D4520~D4522=2 → 轮询等待 PLC 到位 → 复位触发=0。
    /// 机械手自动跳过 X 轴（传入 -1）。
    /// 整个过程走 <see cref="CraneService.MoveAbsoluteAsync"/>，异步不阻塞 UI。
    /// </summary>
    private async Task MoveAbsoluteAsync()
    {
        if (AbsSpeed <= 0 || AbsAccel <= 0 || AbsDecel <= 0)
        {
            Console.WriteLine($"[CraneManualVM] 请先设置绝对速度（当前 speed={AbsSpeed} accel={AbsAccel} decel={AbsDecel}）");
            return;
        }

        var service = await EnsureConnectedServiceAsync();
        var name = CurrentDeviceName;

        // 机械手无 X 轴：X 目标强制传 -1 跳过
        int xTarget = IsCraneSelected ? AbsXTarget : -1;
        int yTarget = AbsYTarget;
        int zTarget = AbsZTarget;

        Console.WriteLine($"[CraneManualVM] [{name}] ▶ 绝对移动 目标 X={xTarget} Y={yTarget} Z={zTarget}");
        Console.WriteLine($"[CraneManualVM] [{name}] 使用绝对速度 speed={AbsSpeed} accel={AbsAccel} decel={AbsDecel}");

        try
        {
            // 先写绝对速度，再写目标坐标，触发移动、轮询到位、自动复位
            await service.SetAbsSpeedAsync(AbsSpeed, AbsAccel, AbsDecel);
            await service.MoveAbsoluteAsync(xTarget, yTarget, zTarget);
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
        if (RelSpeed <= 0 || RelAccel <= 0 || RelDecel <= 0)
        {
            Console.WriteLine($"[CraneManualVM] 相对速度参数无效 speed={RelSpeed} accel={RelAccel} decel={RelDecel}");
            return;
        }

        var service = await EnsureConnectedServiceAsync();
        var name = CurrentDeviceName;
        Console.WriteLine($"[CraneManualVM] [{name}] ▶ 写相对速度 speed={RelSpeed} accel={RelAccel} decel={RelDecel}");
        await service.SetRelSpeedAsync(RelSpeed, RelAccel, RelDecel);
        Console.WriteLine($"[CraneManualVM] [{name}] ✔ 相对速度设置完成");
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
