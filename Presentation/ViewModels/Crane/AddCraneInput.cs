namespace AutomaticOnlineHostComputer.Domain.Models;

/// <summary>新增天车输入模型。</summary>
public sealed class AddCraneInput
{
    public int LineNo { get; set; }
    public int CraneNo { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; }
    public string EncoderIp { get; set; } = string.Empty;
    public int EncoderPort { get; set; }
    public int EncoderNo { get; set; }
    public long OriginX { get; set; }
    public long OriginY { get; set; }
    public long OriginZ { get; set; }
    /// <summary>当前 X 坐标（PLC实时值 D5018~D5019）。编辑后可写入 D3102 触发绝对位移 → TODO 需确认坐标系对齐后开放此功能。</summary>
    public long? CurrentX { get; set; }
    /// <summary>当前 Y 坐标（PLC实时值 D5022）</summary>
    public int? CurrentY { get; set; }
    /// <summary>当前 Z 坐标（PLC实时值 D5025）</summary>
    public int? CurrentZ { get; set; }
    public long Width { get; set; }
    public int AbsXOffset { get; set; }
    public double RatioX { get; set; }
    public double RatioY { get; set; }
    public double RatioZ { get; set; }
    public long StartX { get; set; }
    public long EndX { get; set; }
    public long LimitZP { get; set; }
    public long LimitZN { get; set; }
    public double PulseX { get; set; }
    public string? IconPath { get; set; }
    public int WorkSta { get; set; }
}