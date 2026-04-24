namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Controller;

/// <summary>
/// 控制器寄存器配置表行模型。
/// </summary>
public sealed class ControllerRegisterRowVm
{
    /// <summary>代号。</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>功能。</summary>
    public string Function { get; set; } = string.Empty;

    /// <summary>地址。</summary>
    public int Address { get; set; }

    /// <summary>数据类型。</summary>
    public string DataType { get; set; } = string.Empty;

    /// <summary>读写状态。</summary>
    public string ReadWriteStatus { get; set; } = string.Empty;
}
