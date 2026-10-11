namespace AutomaticOnlineHostComputer.Service;

/// <summary>只用完整板号和序号匹配来源残留，不能把多个“未知”工件当成同一块。</summary>
internal static class WorkpieceCacheIdentity
{
    public static bool Matches(WorkpieceCache candidate, WorkpieceCache workpiece) =>
        !string.IsNullOrWhiteSpace(workpiece.PlateNo) &&
        !string.IsNullOrWhiteSpace(workpiece.Sequence) &&
        string.Equals(candidate.PlateNo, workpiece.PlateNo, StringComparison.Ordinal) &&
        string.Equals(candidate.Sequence, workpiece.Sequence, StringComparison.Ordinal);
}
