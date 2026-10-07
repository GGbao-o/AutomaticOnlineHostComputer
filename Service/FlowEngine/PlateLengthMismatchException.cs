namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 机械手1 取料前，D100 实测板长与任务板长偏差超过容差。
/// <para>
/// 该异常只在<b>尚未下发任何物理命令</b>（未充磁、未移动、未写 PLC）时抛出，
/// 因此调用方可以安全地<b>丢弃本块板的软件任务身份</b>：清 <c>_currentWp</c>、不回退本线缓存，
/// 再暂停本线并弹窗等待人工核对 ST007 上的实物板与 ERP 任务数据。
/// 抛出的时机必须早于 <c>FlowActionContext.MarkCommandSent()</c>，否则会被外层收尾
/// 误判为"命令响应未知"而保留在途工件，无法丢弃。
/// </para>
/// </summary>
public sealed class PlateLengthMismatchException : Exception
{
    public PlateLengthMismatchException(int measuredMm, double expectedMm, int toleranceMm)
        : base($"D100实测板长={measuredMm}mm 与任务板长={expectedMm}mm " +
               $"偏差={Math.Abs(measuredMm - expectedMm):0.##}mm 超过容差{toleranceMm}mm")
    {
        MeasuredMm = measuredMm;
        ExpectedMm = expectedMm;
        ToleranceMm = toleranceMm;
    }

    /// <summary>D100 实测板长（mm）。</summary>
    public int MeasuredMm { get; }

    /// <summary>任务缓存板长（mm），保留 ERP 原始小数。</summary>
    public double ExpectedMm { get; }

    /// <summary>本次判定使用的容差（mm）。</summary>
    public int ToleranceMm { get; }

    /// <summary>偏差绝对值（mm），供弹窗直接显示。</summary>
    public double DeviationMm => Math.Abs(MeasuredMm - ExpectedMm);
}
