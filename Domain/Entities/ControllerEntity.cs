namespace AutomaticOnlineHostComputer.Domain.Entities;

/// <summary>
/// 控制器实体。
/// 对应控制器管理页面的数据模型。
/// </summary>
public sealed class ControllerEntity
{
    /// <summary>控制器主键。</summary>
    public int Id { get; set; }

    /// <summary>控制器名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>控制器类型。</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>IP 地址。</summary>
    public string IP { get; set; } = string.Empty;

    /// <summary>端口。</summary>
    public int Port { get; set; }

    /// <summary>编号。</summary>
    public int No { get; set; }

    /// <summary>是否在线。</summary>
    public bool IsOnline { get; set; }
}
