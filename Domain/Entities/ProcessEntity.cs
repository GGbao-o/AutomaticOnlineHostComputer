namespace AutomaticOnlineHostComputer.Domain.Entities;

/// <summary>
/// 工艺流程实体。
/// 对应工艺管理页面的数据模型。
/// </summary>
public sealed class ProcessEntity
{
    /// <summary>工艺主键。</summary>
    public int Id { get; set; }

    /// <summary>工艺名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>工步编号。</summary>
    public int StepIndex { get; set; }

    /// <summary>工步名称。</summary>
    public string StepName { get; set; } = string.Empty;

    /// <summary>适用设备类型。</summary>
    public string MachineTypeName { get; set; } = string.Empty;

    /// <summary>是否启用。</summary>
    public bool Enable { get; set; } = true;
}
