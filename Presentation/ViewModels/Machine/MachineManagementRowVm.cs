using AutomaticOnlineHostComputer.Presentation.ViewModels;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;

/// <summary>
/// 机器管理页列表行模型。  
/// 继承 <see cref="ObservableObject"/> 以支持 WPF 数据绑定自动刷新。
/// </summary>
public sealed class MachineManagementRowVm : ObservableObject
{
    private int _sourceId;
    /// <summary>数据库主键（machine.id）。</summary>
    public int SourceId { get => _sourceId; set => SetField(ref _sourceId, value); }

    private int _id;
    /// <summary>机器编号。</summary>
    public int Id { get => _id; set => SetField(ref _id, value); }

    private int _lineNo;
    /// <summary>线路号。</summary>
    public int LineNo { get => _lineNo; set => SetField(ref _lineNo, value); }

    private string _stationCode = string.Empty;
    /// <summary>站位编码。</summary>
    public string StationCode { get => _stationCode; set => SetField(ref _stationCode, value); }

    private string _typeName = string.Empty;
    /// <summary>机器类型。</summary>
    public string TypeName { get => _typeName; set => SetField(ref _typeName, value); }

    private string _areaName = string.Empty;
    /// <summary>区域。</summary>
    public string AreaName { get => _areaName; set => SetField(ref _areaName, value); }

    private int _machineNo;
    /// <summary>机号。</summary>
    public int MachineNo { get => _machineNo; set => SetField(ref _machineNo, value); }

    private string _ip = string.Empty;
    /// <summary>IP。</summary>
    public string Ip { get => _ip; set => SetField(ref _ip, value); }

    private int _port;
    /// <summary>端口。</summary>
    public int Port { get => _port; set => SetField(ref _port, value); }

    private string _state = string.Empty;
    /// <summary>
    /// 状态。
    /// 实时可变字段：当机器状态轮询更新时，直接赋值即可触发 UI 刷新。
    /// </summary>
    public string State { get => _state; set => SetField(ref _state, value); }

    private string _name = string.Empty;
    /// <summary>机器名称。</summary>
    public string Name { get => _name; set => SetField(ref _name, value); }

    private string _position = string.Empty;
    /// <summary>机器位置描述。</summary>
    public string Position { get => _position; set => SetField(ref _position, value); }

    private string _thumb = string.Empty;
    /// <summary>缩略图路径或名称。</summary>
    public string Thumb { get => _thumb; set => SetField(ref _thumb, value); }

    private double _x;
    /// <summary>X 坐标。</summary>
    public double X { get => _x; set => SetField(ref _x, value); }

    private double _y;
    /// <summary>Y 坐标。</summary>
    public double Y { get => _y; set => SetField(ref _y, value); }

    private double _z;
    /// <summary>Z 坐标。</summary>
    public double Z { get => _z; set => SetField(ref _z, value); }

    private double _xOffset;
    /// <summary>X 轴偏移。</summary>
    public double XOffset { get => _xOffset; set => SetField(ref _xOffset, value); }

    private double _yOffset;
    /// <summary>Y 轴偏移。</summary>
    public double YOffset { get => _yOffset; set => SetField(ref _yOffset, value); }

    private double _zOffset;
    /// <summary>Z 轴偏移。</summary>
    public double ZOffset { get => _zOffset; set => SetField(ref _zOffset, value); }

    private int _safeZDown;
    /// <summary>下降安全Z。</summary>
    public int SafeZDown { get => _safeZDown; set => SetField(ref _safeZDown, value); }

    private int _safeZUp;
    /// <summary>上升安全Z。</summary>
    public int SafeZUp { get => _safeZUp; set => SetField(ref _safeZUp, value); }

    private int _absolutePos;
    /// <summary>绝对坐标。</summary>
    public int AbsolutePos { get => _absolutePos; set => SetField(ref _absolutePos, value); }

    private double _shake;
    /// <summary>抖动距离。</summary>
    public double Shake { get => _shake; set => SetField(ref _shake, value); }

    private string _processRange = string.Empty;
    /// <summary>加工范围。</summary>
    public string ProcessRange { get => _processRange; set => SetField(ref _processRange, value); }

    private string _titleText = "机器管理";
    /// <summary>页面标题。</summary>
    public string TitleText { get => _titleText; set => SetField(ref _titleText, value); }

    private string _addButtonText = "添加机器";
    /// <summary>添加按钮文本。</summary>
    public string AddButtonText { get => _addButtonText; set => SetField(ref _addButtonText, value); }

    private string _editButtonText = "编辑机器";
    /// <summary>编辑按钮文本。</summary>
    public string EditButtonText { get => _editButtonText; set => SetField(ref _editButtonText, value); }

    private string _deleteButtonText = "删除机器";
    /// <summary>删除按钮文本。</summary>
    public string DeleteButtonText { get => _deleteButtonText; set => SetField(ref _deleteButtonText, value); }

    private string _refreshIconButtonText = "刷新图标";
    /// <summary>刷新图标按钮文本。</summary>
    public string RefreshIconButtonText { get => _refreshIconButtonText; set => SetField(ref _refreshIconButtonText, value); }
}
