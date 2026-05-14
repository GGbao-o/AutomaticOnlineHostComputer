namespace AutomaticOnlineHostComputer.Domain.Models;

/// <summary>新增工艺输入模型。</summary>
public sealed class AddProcessInput
{
    public string ProcessName { get; set; } = string.Empty;
    public int StepNo { get; set; }
    public string StepName { get; set; } = string.Empty;
    public int ExecuteTime { get; set; }
    public string Device { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool ZAxisBackHome { get; set; }
    public string? SafePosition { get; set; }
    public int MachineNo { get; set; }
}