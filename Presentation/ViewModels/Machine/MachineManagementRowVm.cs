namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;

/// <summary>
/// 机器管理页列表行模型。
/// </summary>
public sealed class MachineManagementRowVm
{
    /// <summary>数据库主键（machine.id）。</summary>
    public int SourceId { get; set; }

    /// <summary>机器编号。</summary>
    public int Id { get; set; }

    /// <summary>线路号。</summary>
    public int LineNo { get; set; }

    /// <summary>站位编码。</summary>
    public string StationCode { get; set; } = string.Empty;

    /// <summary>机器类型。</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>区域。</summary>
    public string AreaName { get; set; } = string.Empty;

    /// <summary>机号。</summary>
    public int MachineNo { get; set; }

    /// <summary>IP。</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>端口。</summary>
    public int Port { get; set; }

    /// <summary>状态。</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>机器名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>机器位置描述。</summary>
    public string Position { get; set; } = string.Empty;

    /// <summary>缩略图路径或名称。</summary>
    public string Thumb { get; set; } = string.Empty;

    /// <summary>X 坐标。</summary>
    public double X { get; set; }

    /// <summary>Y 坐标。</summary>
    public double Y { get; set; }

    /// <summary>Z 坐标。</summary>
    public double Z { get; set; }

    /// <summary>X 轴偏移。</summary>
    public double XOffset { get; set; }

    /// <summary>Y 轴偏移。</summary>
    public double YOffset { get; set; }

    /// <summary>Z 轴偏移。</summary>
    public double ZOffset { get; set; }

    /// <summary>下降安全Z。</summary>
    public int SafeZDown { get; set; }

    /// <summary>上升安全Z。</summary>
    public int SafeZUp { get; set; }

    /// <summary>绝对坐标。</summary>
    public int AbsolutePos { get; set; }

    /// <summary>抖动距离。</summary>
    public double Shake { get; set; }

    /// <summary>加工范围。</summary>
    public string ProcessRange { get; set; } = string.Empty;

    /// <summary>页面标题。</summary>
    public string TitleText { get; set; } = "机器管理";

    /// <summary>添加按钮文本。</summary>
    public string AddButtonText { get; set; } = "添加机器";

    /// <summary>编辑按钮文本。</summary>
    public string EditButtonText { get; set; } = "编辑机器";

    /// <summary>删除按钮文本。</summary>
    public string DeleteButtonText { get; set; } = "删除机器";

    /// <summary>刷新图标按钮文本。</summary>
    public string RefreshIconButtonText { get; set; } = "刷新图标";
}
