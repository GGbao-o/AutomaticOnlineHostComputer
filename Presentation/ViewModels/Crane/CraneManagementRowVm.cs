using AutomaticOnlineHostComputer.Presentation.ViewModels;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Crane;

/*
 * ╔═══════════════════════════════════════════════════════════════════════╗
 * ║                    CraneManagementRowVm                               ║
 * ╠═══════════════════════════════════════════════════════════════════════╣
 * ║  改造说明：                                                            ║
 * ║  原来是普通 POCO（Plain Old CLR Object），属性用自动属性实现，           ║
 * ║  没有 INotifyPropertyChanged，DataGrid 列表数据改变后 UI 不更新。       ║
 * ║                                                                       ║
 * ║  现在继承 ObservableObject，每个属性改为"私有字段 + SetField 写法"：    ║
 * ║    private T _field;                                                  ║
 * ║    public T Property { get => _field; set => SetField(ref _field, value); } ║
 * ║                                                                       ║
 * ║  这样当某一行的状态（如 State）在后台线程更新后，                        ║
 * ║  DataGrid 对应单元格会自动刷新，无需重置整个 ItemsSource。              ║
 * ╚═══════════════════════════════════════════════════════════════════════╝
 */

/// <summary>
/// 天车管理页列表行模型。
/// 继承 <see cref="ObservableObject"/> 以支持 WPF 数据绑定自动刷新。
/// </summary>
public sealed class CraneManagementRowVm : ObservableObject
{
    // ── 私有字段（backing field）+ 公开属性 ───────────────────────────────
    // 每个属性都通过 SetField 写入：
    //   1. 比较新旧值，相同则不触发事件（避免无效刷新）
    //   2. 值变化时自动触发 PropertyChanged，WPF 立即更新对应 UI

    private int _sourceId;
    /// <summary>数据库主键（crane.id）。</summary>
    public int SourceId { get => _sourceId; set => SetField(ref _sourceId, value); }

    private int _id;
    /// <summary>天车编号。</summary>
    public int Id { get => _id; set => SetField(ref _id, value); }

    private int _lineNo;
    /// <summary>线路号。</summary>
    public int LineNo { get => _lineNo; set => SetField(ref _lineNo, value); }

    private int _craneNo;
    /// <summary>天车机号。</summary>
    public int CraneNo { get => _craneNo; set => SetField(ref _craneNo, value); }

    private string _name = string.Empty;
    /// <summary>天车名称。</summary>
    public string Name { get => _name; set => SetField(ref _name, value); }

    private double _x;
    /// <summary>X 坐标。</summary>
    public double X { get => _x; set => SetField(ref _x, value); }

    private double _y;
    /// <summary>Y 坐标。</summary>
    public double Y { get => _y; set => SetField(ref _y, value); }

    private double _z;
    /// <summary>Z 坐标（原点标定值）。</summary>
    public double Z { get => _z; set => SetField(ref _z, value); }

    // ── 当前位置（从PLC实时读取，D5018/D5022/D5025）─────────────

    private long? _currentX;
    /// <summary>当前 X 坐标（PLC 实时值 D5018~D5019）。编辑后可直接写入 D3102 触发绝对移动 → TODO 需确认坐标系对齐。</summary>
    public long? CurrentX { get => _currentX; set { if (SetField(ref _currentX, value)) { OnPropertyChanged(nameof(DeltaX)); OnPropertyChanged(nameof(DeltaY)); OnPropertyChanged(nameof(DeltaZ)); } } }

    private int? _currentY;
    public int? CurrentY { get => _currentY; set { if (SetField(ref _currentY, value)) { OnPropertyChanged(nameof(DeltaX)); OnPropertyChanged(nameof(DeltaY)); OnPropertyChanged(nameof(DeltaZ)); } } }

    private int? _currentZ;
    public int? CurrentZ { get => _currentZ; set { if (SetField(ref _currentZ, value)) { OnPropertyChanged(nameof(DeltaX)); OnPropertyChanged(nameof(DeltaY)); OnPropertyChanged(nameof(DeltaZ)); } } }

    /// <summary>X 偏移 = 标定X - 当前X（天车距原点多远）</summary>
    public string DeltaX => CurrentX.HasValue ? (X - CurrentX.Value).ToString("+#;-#;0") : "—";
    /// <summary>Y 偏移</summary>
    public string DeltaY => CurrentY.HasValue ? (Y - CurrentY.Value).ToString("+#;-#;0") : "—";
    /// <summary>Z 偏移</summary>
    public string DeltaZ => CurrentZ.HasValue ? (Z - CurrentZ.Value).ToString("+#;-#;0") : "—";

    private string _ip = string.Empty;
    /// <summary>IP 地址。</summary>
    public string Ip { get => _ip; set => SetField(ref _ip, value); }

    private int _port;
    /// <summary>天车端口。</summary>
    public int Port { get => _port; set => SetField(ref _port, value); }

    private string _encoderIp = string.Empty;
    /// <summary>编码器 IP。</summary>
    public string EncoderIp { get => _encoderIp; set => SetField(ref _encoderIp, value); }

    private int _encoderPort;
    /// <summary>编码器端口。</summary>
    public int EncoderPort { get => _encoderPort; set => SetField(ref _encoderPort, value); }

    private int _encoderNo;
    /// <summary>编码器机号。</summary>
    public int EncoderNo { get => _encoderNo; set => SetField(ref _encoderNo, value); }

    private double _width;
    /// <summary>天车宽度。</summary>
    public double Width { get => _width; set => SetField(ref _width, value); }

    private int _absXOffset;
    /// <summary>绝对位置偏移。</summary>
    public int AbsXOffset { get => _absXOffset; set => SetField(ref _absXOffset, value); }

    private double _ratioX;
    /// <summary>X轴减速比。</summary>
    public double RatioX { get => _ratioX; set => SetField(ref _ratioX, value); }

    private double _ratioY;
    /// <summary>Y轴减速比。</summary>
    public double RatioY { get => _ratioY; set => SetField(ref _ratioY, value); }

    private double _ratioZ;
    /// <summary>Z轴减速比。</summary>
    public double RatioZ { get => _ratioZ; set => SetField(ref _ratioZ, value); }

    private long _startX;
    /// <summary>X轴起始位置。</summary>
    public long StartX { get => _startX; set => SetField(ref _startX, value); }

    private long _endX;
    /// <summary>X轴结束位置。</summary>
    public long EndX { get => _endX; set => SetField(ref _endX, value); }

    private long _limitZP;
    /// <summary>Z轴正限位。</summary>
    public long LimitZP { get => _limitZP; set => SetField(ref _limitZP, value); }

    private long _limitZN;
    /// <summary>Z轴负限位。</summary>
    public long LimitZN { get => _limitZN; set => SetField(ref _limitZN, value); }

    private double _pulseX;
    /// <summary>X轴螺距。</summary>
    public double PulseX { get => _pulseX; set => SetField(ref _pulseX, value); }

    private string _state = string.Empty;
    /// <summary>
    /// 天车状态文本。
    /// 这是最典型的需要实时更新的字段：
    /// 后台轮询到天车状态变化时，只需 row.State = "运行中"，
    /// DataGrid 中对应单元格就会立刻刷新，无需重新查询数据库。
    /// </summary>
    public string State { get => _state; set => SetField(ref _state, value); }

    // ── 页面静态文本（初始化后不会变化，但保持一致的写法） ────────────────

    private string _titleText = "天车管理";
    /// <summary>页面标题。</summary>
    public string TitleText { get => _titleText; set => SetField(ref _titleText, value); }

    private string _addButtonText = "添加天车";
    /// <summary>添加按钮文本。</summary>
    public string AddButtonText { get => _addButtonText; set => SetField(ref _addButtonText, value); }

    private string _editButtonText = "编辑天车";
    /// <summary>编辑按钮文本。</summary>
    public string EditButtonText { get => _editButtonText; set => SetField(ref _editButtonText, value); }

    private string _deleteButtonText = "删除天车";
    /// <summary>删除按钮文本。</summary>
    public string DeleteButtonText { get => _deleteButtonText; set => SetField(ref _deleteButtonText, value); }

    private string _refreshIconButtonText = "刷新图标";
    /// <summary>刷新图标按钮文本。</summary>
    public string RefreshIconButtonText { get => _refreshIconButtonText; set => SetField(ref _refreshIconButtonText, value); }
}
