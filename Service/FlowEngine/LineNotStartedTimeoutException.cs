namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 后天车下料分流前，等待本线启动（后端引擎退出暂停）超过配置上限。
/// <para>
/// 抛出时机固定在<b>拿到 M817/M720（或 M818/M821）两把分流锁之前、任何 XY 移动与退磁之前</b>，
/// 此时天车停在斜床上方安全 Z、保持持件，只持有后天车锁。因此调用方可以安全地
/// <b>作废本趟分流</b>：清该斜床 PLC 下发数据、内存状态回空闲、由外层 finally 释放后天车锁，
/// 天车不动，等人工放磁取下工件后启动线路继续。
/// </para>
/// 注意：本异常必须在分流区 <c>try</c> 之内抛出，否则外层 <c>finally</c> 不会执行，
/// 后天车锁将无法释放。
/// </summary>
public sealed class LineNotStartedTimeoutException : Exception
{
    public LineNotStartedTimeoutException(string lineName, string bedCode, int timeoutMs)
        : base($"{lineName}在{bedCode}下料分流前等待本线启动超过{timeoutMs / 1000}秒，本线仍处于暂停状态")
    {
        LineName = lineName;
        BedCode = bedCode;
        TimeoutMs = timeoutMs;
    }

    /// <summary>线体名称，例如"1号线"。</summary>
    public string LineName { get; }

    /// <summary>发生等待的斜床编号，例如 ST108。</summary>
    public string BedCode { get; }

    /// <summary>本次等待使用的上限（毫秒）。</summary>
    public int TimeoutMs { get; }
}
