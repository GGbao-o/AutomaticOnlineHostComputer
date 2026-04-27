using AutomaticOnlineHostComputer.Presentation.ViewModels;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Controller;

/// <summary>
/// 控制器管理页列表行模型。
/// 继承 <see cref="ObservableObject"/> 以支持 WPF 数据绑定自动刷新。
/// </summary>
public sealed class ControllerManagementRowVm : ObservableObject
{
    private int _sourceId;
    /// <summary>数据库主键（controller.id）。</summary>
    public int SourceId { get => _sourceId; set => SetField(ref _sourceId, value); }

    private int _id;
    /// <summary>机号。</summary>
    public int Id { get => _id; set => SetField(ref _id, value); }

    private int _deviceNo;
    /// <summary>设备号。</summary>
    public int DeviceNo { get => _deviceNo; set => SetField(ref _deviceNo, value); }

    private string _name = string.Empty;
    /// <summary>控制器名称。</summary>
    public string Name { get => _name; set => SetField(ref _name, value); }

    private string _type = string.Empty;
    /// <summary>设备类型。</summary>
    public string Type { get => _type; set => SetField(ref _type, value); }

    private string _ip = string.Empty;
    /// <summary>IP 地址。</summary>
    public string Ip { get => _ip; set => SetField(ref _ip, value); }

    private int _port;
    /// <summary>端口。</summary>
    public int Port { get => _port; set => SetField(ref _port, value); }

    private string _state = string.Empty;
    /// <summary>
    /// 设备状态文本。
    /// 实时可变字段：当设备状态轮询更新时，直接赋值即可触发 UI 刷新。
    /// </summary>
    public string State { get => _state; set => SetField(ref _state, value); }

    private string _titleText = "控制器管理";
    /// <summary>页面标题。</summary>
    public string TitleText { get => _titleText; set => SetField(ref _titleText, value); }

    private string _addButtonText = "添加设备";
    /// <summary>添加按钮文本。</summary>
    public string AddButtonText { get => _addButtonText; set => SetField(ref _addButtonText, value); }

    private string _editButtonText = "编辑设备";
    /// <summary>编辑按钮文本。</summary>
    public string EditButtonText { get => _editButtonText; set => SetField(ref _editButtonText, value); }

    private string _deleteButtonText = "删除设备";
    /// <summary>删除按钮文本。</summary>
    public string DeleteButtonText { get => _deleteButtonText; set => SetField(ref _deleteButtonText, value); }

    private string _refreshIconButtonText = "刷新图标";
    /// <summary>刷新图标按钮文本。</summary>
    public string RefreshIconButtonText { get => _refreshIconButtonText; set => SetField(ref _refreshIconButtonText, value); }
}
