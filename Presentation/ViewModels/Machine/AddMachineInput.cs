namespace AutomaticOnlineHostComputer.Domain.Models;

/// <summary>新增机器输入模型。</summary>
public sealed class AddMachineInput
{
    public int LineNo { get; set; }
    public string? StationCode { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string? AreaName { get; set; }
    public int MachineNo { get; set; }
    public string? Ip { get; set; }
    public int Port { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    /// <summary>当前 X 坐标（PLC实时值）。编辑后可通过 CraneService.MoveAbsoluteAsync 直接走绝对位移 → TODO 需确认坐标系对齐后开放。</summary>
    public int? CurrentX { get; set; }
    /// <summary>当前 Y 坐标（PLC实时值）</summary>
    public int? CurrentY { get; set; }
    /// <summary>当前 Z 坐标（PLC实时值）</summary>
    public int? CurrentZ { get; set; }
    public int SafeZDown { get; set; }
    public int SafeZUp { get; set; }
    public int AbsolutePos { get; set; }
    public string? XDis { get; set; }
    public string? YDis { get; set; }
    public string? ZDis { get; set; }
    public int DisShake { get; set; }
    public string? ProcessRange { get; set; }
    public string? IconPath { get; set; }
    public int State { get; set; }
    
    
}