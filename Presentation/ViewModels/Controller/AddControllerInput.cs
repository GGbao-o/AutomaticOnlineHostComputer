namespace AutomaticOnlineHostComputer.Domain.Models;

/// <summary>新增控制器输入模型。</summary>
public sealed class AddControllerInput
{
    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string? Ip { get; set; }
    public int Port { get; set; }
    public int DeviceNo { get; set; }
    public string? IconPath { get; set; }
    public int State { get; set; }
}