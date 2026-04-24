namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;

/// <summary>
/// 机器参数配置表行模型。
/// </summary>
public sealed class MachineParameterRowVm
{
    /// <summary>参数代号。</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>参数编号。</summary>
    public int No { get; set; }

    /// <summary>参数名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>参数地址。</summary>
    public int Addr { get; set; }

    /// <summary>参数表达式。</summary>
    public string Exp { get; set; } = string.Empty;
}
