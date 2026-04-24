namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Controller;

/// <summary>
/// 控制器管理页列表行模型。
/// </summary>
public sealed class ControllerManagementRowVm
{
    /// <summary>数据库主键（controller.id）。</summary>
    public int SourceId { get; set; }

    /// <summary>机号。</summary>
    public int Id { get; set; }

    /// <summary>设备号。</summary>
    public int DeviceNo { get; set; }

    /// <summary>控制器名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>缩略图路径或名称。</summary>
    public string Thumb { get; set; } = string.Empty;

    /// <summary>设备类型。</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>IP 地址。</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>端口。</summary>
    public int Port { get; set; }

    /// <summary>设备状态文本。</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>页面标题。</summary>
    public string TitleText { get; set; } = "控制器管理";

    /// <summary>添加按钮文本。</summary>
    public string AddButtonText { get; set; } = "添加设备";

    /// <summary>编辑按钮文本。</summary>
    public string EditButtonText { get; set; } = "编辑设备";

    /// <summary>删除按钮文本。</summary>
    public string DeleteButtonText { get; set; } = "删除设备";

    /// <summary>刷新图标按钮文本。</summary>
    public string RefreshIconButtonText { get; set; } = "刷新图标";
}
