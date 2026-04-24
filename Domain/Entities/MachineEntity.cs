using System.ComponentModel.DataAnnotations;

namespace AutomaticOnlineHostComputer.Domain.Entities;

/// <summary>
/// 机器实体。
/// 对应机器管理页面的新增/编辑/删除数据模型。
/// </summary>
public sealed class MachineEntity
{
    /// <summary>机器主键。</summary>
    public int Id { get; set; }

    /// <summary>机器名称。</summary>
    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>所在线路或区域。</summary>
    public string Position { get; set; } = string.Empty;

    /// <summary>缩略图或图标路径。</summary>
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

    /// <summary>抖动距离。</summary>
    public double Shake { get; set; }

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;
}
