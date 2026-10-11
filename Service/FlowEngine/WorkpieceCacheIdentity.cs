namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 异常作废时只用完整板号和序号定位本板残留，不能把多个“未知”工件当成同一块。
/// 长度、直径等属性可能在任务执行中补读或修正，因此不使用整个WorkpieceCache值相等来删除来源。
/// 同板号不同序号是不同任务；缺任一身份字段返回不匹配，避免误删下一板或匿名M700任务。
/// </summary>
internal static class WorkpieceCacheIdentity
{
    public static bool Matches(WorkpieceCache candidate, WorkpieceCache workpiece) =>
        !string.IsNullOrWhiteSpace(workpiece.PlateNo) &&
        !string.IsNullOrWhiteSpace(workpiece.Sequence) &&
        string.Equals(candidate.PlateNo, workpiece.PlateNo, StringComparison.Ordinal) &&
        string.Equals(candidate.Sequence, workpiece.Sequence, StringComparison.Ordinal);
}
