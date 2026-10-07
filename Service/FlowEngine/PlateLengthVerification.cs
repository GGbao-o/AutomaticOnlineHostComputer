namespace AutomaticOnlineHostComputer.Service;

/// <summary>板长校验结论。</summary>
public enum PlateLengthVerdict
{
    /// <summary>实测板长与任务板长偏差在容差内，允许继续取料。</summary>
    Pass,

    /// <summary>偏差超过容差：必须拒绝取料，并丢弃本块板的软件任务身份。</summary>
    Mismatch,

    /// <summary>缺少可比对的基准（任务板长无效，或 D100 本次没有可用读数），本次不校验。</summary>
    NotComparable
}

/// <summary>
/// 机械手1 取料前的板长校验：把总上料架（MC63）D100 的居中实测板长与任务缓存板长比对。
/// <para>
/// 纯函数、无 IO、无状态，容差边界由单元测试直接钉死；判定规则只此一处，
/// 1/2 号线前端引擎共用，避免两条线各写一份判断后现场行为不一致。
/// </para>
/// </summary>
public static class PlateLengthVerification
{
    /// <summary>
    /// 判定实测板长与任务板长是否一致。
    /// </summary>
    /// <param name="measuredMm">D100 实测板长（mm，整数）。小于等于 0 视为本次没有可用读数。</param>
    /// <param name="taskLengthMm">
    /// 任务缓存板长（mm）。任务板长来自 ERP/人工建单，类型为 double，因此保留小数直接比较，
    /// 不做取整，避免引入额外偏差。小于等于 0 视为没有比对基准。
    /// </param>
    /// <param name="toleranceMm">允许偏差（mm）。偏差正好等于该值算通过；传入负值时按 0 处理。</param>
    public static PlateLengthVerdict Evaluate(int measuredMm, double taskLengthMm, int toleranceMm)
    {
        // 两种"没法比"的情形：
        //   ① 任务板长无效 —— 没有基准，无从判断；
        //   ② D100 读到 0 —— 板长真值不可能是 0。CenteringRackService 在 MC 短报文时
        //      返回 0 而不是抛异常，若把它当 Mismatch，一次通信抖动就会丢弃任务并停线，
        //      因此按"校验不可用"放行，只记日志。
        if (taskLengthMm <= 0 || measuredMm <= 0)
            return PlateLengthVerdict.NotComparable;

        int tolerance = toleranceMm < 0 ? 0 : toleranceMm;
        return Math.Abs(measuredMm - taskLengthMm) <= tolerance
            ? PlateLengthVerdict.Pass
            : PlateLengthVerdict.Mismatch;
    }
}
