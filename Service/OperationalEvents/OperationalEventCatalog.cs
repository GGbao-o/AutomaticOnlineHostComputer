using System.Collections.ObjectModel;

namespace AutomaticOnlineHostComputer.Service.OperationalEvents;

public sealed record OperationalEventDefinition
{
    public OperationalEventDefinition(
        string eventCode,
        string title,
        OperationalEventSeverity defaultSeverity,
        OperationalEventCategory defaultCategory,
        IEnumerable<string> requiredActions,
        IEnumerable<string> forbiddenActions,
        string continueCondition)
    {
        EventCode = eventCode;
        Title = title;
        DefaultSeverity = defaultSeverity;
        DefaultCategory = defaultCategory;
        RequiredActions = EvidenceCollection.Freeze(requiredActions);
        ForbiddenActions = EvidenceCollection.Freeze(forbiddenActions);
        ContinueCondition = continueCondition;
    }

    public string EventCode { get; }

    public string Title { get; }

    public OperationalEventSeverity DefaultSeverity { get; }

    public OperationalEventCategory DefaultCategory { get; }

    public IReadOnlyList<string> RequiredActions { get; }

    public IReadOnlyList<string> ForbiddenActions { get; }

    public string ContinueCondition { get; }
}

public sealed class OperationalEventCatalog
{
    private const string GenericRequired = "记录事件编号、动作编号和发生时间，并按页面证据逐项核对现场状态。";
    private const string GenericForbidden = "禁止在证据未闭环时清除工件身份、跳过安全检查或直接重放设备命令。";
    private const string GenericContinue = "仅在页面列出的未知项已人工确认、设备处于安全位置且原业务条件允许时继续。";
    private const string PhysicalUnknownForbidden = "禁止仅凭软件false变量重复取料或清工件身份；禁止把通信超时解释为命令未执行。";

    private readonly IReadOnlyDictionary<string, OperationalEventDefinition> _definitions;

    public OperationalEventCatalog()
    {
        OperationalEventDefinition[] definitions =
        [
            Define("LEGACY_SAFETY_ALARM", "既有安全异常", OperationalEventSeverity.Critical, OperationalEventCategory.Safety,
                "按原安全异常提示确认设备、工件和人员状态。", "禁止绕过原暂停、互锁或报警处理。"),
            Define("LEGACY_WARNING", "既有黄色警告", OperationalEventSeverity.Warning, OperationalEventCategory.Safety,
                "核对警告来源、关联工位和当前动作是否仍可安全继续。", "禁止忽略重复警告或以重启页面代替现场检查。"),
            Define("LEGACY_EMERGENCY", "应急操作结果", OperationalEventSeverity.Critical, OperationalEventCategory.Emergency,
                "核对应急动作每一步结果以及仍需人工确认的设备位置。", "禁止假定应急调用返回即代表所有设备已到安全位。"),
            Define("ENGINE_FINAL_FAILURE", "流程最终失败", OperationalEventSeverity.Error, OperationalEventCategory.FinalFailure,
                "核对最后成功检查点、失败阶段、工件所有权和原业务暂停状态。", "禁止绕过原状态机重新发起同一动作。"),
            Define("CRANE_XY_FINE_TUNE_FAILED", "XY绝对编码器微调失败", OperationalEventSeverity.Error, OperationalEventCategory.FineTune,
                "核对显示坐标、绝对坐标、最后发送目标、偏差、容差和尝试次数。", "禁止在坐标或Z高度未知时手动重复微调命令。"),
            Define("CRANE_MAGNET_ON_RESPONSE_UNKNOWN", "充磁命令响应未知", OperationalEventSeverity.Critical, OperationalEventCategory.PhysicalUnknown,
                "现场确认磁铁实际状态、X11有效读数、工件位置和Z高度。", PhysicalUnknownForbidden),
            Define("CRANE_MAGNET_OFF_FAILED", "退磁失败", OperationalEventSeverity.Critical, OperationalEventCategory.Magnet,
                "现场确认磁铁是否仍吸持工件、目标位置和Z高度。", "禁止在退磁结果未知时移动工件所有权或通知已放料。"),
            Define("CRANE_X11_READ_FAILED", "X11最终读取失败", OperationalEventSeverity.Error, OperationalEventCategory.Sensor,
                "核对X11最后值、读取有效性、尝试次数并现场确认是否持件。", PhysicalUnknownForbidden),
            Define("CRANE_X11_NOT_CONFIRMED", "X11未确认持件", OperationalEventSeverity.Error, OperationalEventCategory.Sensor,
                "核对X11读取有效性、工件是否实际吸附及充磁命令结果。", PhysicalUnknownForbidden),
            Define("PRESSURE_STOP_DETECTED", "Z下压保护触发", OperationalEventSeverity.Critical, OperationalEventCategory.Motion,
                "确认Z轴是否仍在低位、当前持件和冲突区域，再核对原恢复步骤。", "禁止在Z高度或持件状态未知时执行水平移动。"),
            Define("PRESSURE_STOP_RECOVERED", "下压保护自动恢复", OperationalEventSeverity.Information, OperationalEventCategory.AutomaticRecovery,
                "逐项核对自动恢复步骤和恢复后复核证据。", "禁止只凭“已恢复”文字跳过Z高度、持件和冲突区确认。"),
            Define("PHYSICAL_HANDOFF_NOT_CLOSED", "物理交接未闭环", OperationalEventSeverity.Critical, OperationalEventCategory.PhysicalUnknown,
                "现场确认来源、目标、当前持件、已放料和缓存通知的真实状态。", PhysicalUnknownForbidden),
            Define("DEVICE_TRANSIENT_FAILURE", "设备连续通信失败", OperationalEventSeverity.Warning, OperationalEventCategory.Communication,
                "核对设备号、失败操作、连续次数和最后成功通信时间。", "禁止因监控事件自行改变原重试周期、连接或设备命令。"),
            Define("DEVICE_CONNECTION_RECOVERED", "设备通信恢复", OperationalEventSeverity.Information, OperationalEventCategory.AutomaticRecovery,
                "确认通信恢复后的首个有效结果及原业务是否继续。", "禁止把连接恢复等同于未完成物理动作已经成功。"),
            Define("STAGE_STALLED", "阶段等待超过观察阈值", OperationalEventSeverity.Warning, OperationalEventCategory.StageStall,
                "核对等待条件、寄存器或锁证据以及原业务超时是否仍在运行。", "禁止通过修改监控状态跳过原等待条件。"),
            Define("STAGE_RECOVERED", "阶段恢复推进", OperationalEventSeverity.Information, OperationalEventCategory.AutomaticRecovery,
                "核对阶段完成条件和恢复后的下一个成功检查点。", "禁止把阶段推进解释为整个工件流程已经完成。")
        ];

        _definitions = new ReadOnlyDictionary<string, OperationalEventDefinition>(
            definitions.ToDictionary(item => item.EventCode, StringComparer.Ordinal));
        Definitions = Array.AsReadOnly(definitions.ToArray());
    }

    public static OperationalEventCatalog Default { get; } = new();

    public IReadOnlyList<OperationalEventDefinition> Definitions { get; }

    public bool TryGet(string eventCode, out OperationalEventDefinition definition) =>
        _definitions.TryGetValue(eventCode ?? string.Empty, out definition!);

    public OperationalEventDefinition GetRequired(string eventCode) =>
        _definitions.TryGetValue(eventCode ?? string.Empty, out OperationalEventDefinition? definition)
            ? definition
            : throw new KeyNotFoundException($"未定义事件代码: {eventCode}");

    public OperationalEventDefinition Resolve(OperationalEvent item)
    {
        if (_definitions.TryGetValue(item.EventCode ?? string.Empty, out OperationalEventDefinition? known))
        {
            return new OperationalEventDefinition(
                known.EventCode,
                NonBlank(item.Title, known.Title),
                item.Severity,
                item.Category,
                known.RequiredActions,
                known.ForbiddenActions,
                known.ContinueCondition);
        }

        return new OperationalEventDefinition(
            NonBlank(item.EventCode, "UNKNOWN_OPERATIONAL_EVENT"),
            NonBlank(item.Title, "未登记生产异常"),
            item.Severity,
            item.Category,
            [GenericRequired],
            [GenericForbidden],
            GenericContinue);
    }

    private static OperationalEventDefinition Define(
        string code,
        string title,
        OperationalEventSeverity severity,
        OperationalEventCategory category,
        string required,
        string forbidden) =>
        new(code, title, severity, category, [required, GenericRequired], [forbidden, GenericForbidden], GenericContinue);

    private static string NonBlank(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
