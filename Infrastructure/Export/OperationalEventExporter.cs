using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Infrastructure.Export;

public static class OperationalEventExporter
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly IReadOnlyList<CsvColumn> CsvColumns = CreateCsvColumns();

    public static byte[] BuildCsv(IReadOnlyList<OperationalEvent> events)
    {
        OperationalEvent[] snapshot = Snapshot(events);
        var builder = new StringBuilder();
        AppendCsvRecord(builder, CsvColumns.Select(column => column.Name));
        foreach (OperationalEvent item in snapshot)
        {
            AppendCsvRecord(builder, CsvColumns.Select(column => FormatCsvValue(column.Select(item))));
        }

        byte[] content = Encoding.UTF8.GetBytes(builder.ToString());
        byte[] preamble = Encoding.UTF8.GetPreamble();
        var result = new byte[preamble.Length + content.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(content, 0, result, preamble.Length, content.Length);
        return result;
    }

    public static string BuildText(
        IReadOnlyList<OperationalEvent> events,
        OperationalEventFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        OperationalEvent[] snapshot = Snapshot(events);
        if (snapshot.Length == 0)
        {
            return "无可导出事件";
        }

        var builder = new StringBuilder();
        const string separator = "================================================================================";
        for (int index = 0; index < snapshot.Length; index++)
        {
            OperationalEvent item = snapshot[index];
            if (index > 0)
            {
                builder.AppendLine().AppendLine(separator).AppendLine();
            }

            builder.Append("事件 ").Append(index + 1).Append('/').AppendLine(snapshot.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append("EventId: ").AppendLine(item.EventId);
            builder.Append("首次时间: ").AppendLine(FormatDate(item.FirstOccurredAtUtc));
            builder.Append("最近时间: ").AppendLine(FormatDate(item.LastOccurredAtUtc));
            builder.Append("累计次数: ").AppendLine(item.OccurrenceCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine(formatter.FormatFullDetail(item));
        }

        return builder.ToString().TrimEnd();
    }

    public static Task WriteCsvAsync(
        string path,
        IReadOnlyList<OperationalEvent> events,
        CancellationToken cancellationToken = default)
    {
        OperationalEvent[] snapshot = Snapshot(events);
        byte[] content = BuildCsv(snapshot);
        return File.WriteAllBytesAsync(path, content, cancellationToken);
    }

    public static Task WriteTextAsync(
        string path,
        IReadOnlyList<OperationalEvent> events,
        OperationalEventFormatter formatter,
        CancellationToken cancellationToken = default)
    {
        OperationalEvent[] snapshot = Snapshot(events);
        string content = BuildText(snapshot, formatter);
        return File.WriteAllTextAsync(path, content, Utf8WithBom, cancellationToken);
    }

    public static string MakeCsvCellSafe(string input)
    {
        input ??= string.Empty;
        string trimmedStart = input.TrimStart(' ', '\t', '\r', '\n');
        return trimmedStart.Length > 0 && "=+-@".Contains(trimmedStart[0], StringComparison.Ordinal)
            ? "'" + input
            : input;
    }

    private static OperationalEvent[] Snapshot(IReadOnlyList<OperationalEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events is OperationalEvent[] array)
        {
            return (OperationalEvent[])array.Clone();
        }

        return events.ToArray();
    }

    private static void AppendCsvRecord(StringBuilder builder, IEnumerable<string> fields)
    {
        bool first = true;
        foreach (string field in fields)
        {
            if (!first)
            {
                builder.Append(',');
            }
            first = false;
            builder.Append(EscapeCsv(field));
        }
        builder.Append("\r\n");
    }

    private static string EscapeCsv(string value)
    {
        value ??= string.Empty;
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }
        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    private static string FormatCsvValue(CsvValue value) =>
        value.IsExternalText ? MakeCsvCellSafe(value.Value) : value.Value;

    private static IReadOnlyList<CsvColumn> CreateCsvColumns()
    {
        var columns = new List<CsvColumn>();
        void Scalar(string name, Func<OperationalEvent, object?> selector) =>
            columns.Add(new CsvColumn(name, item => CsvValue.Scalar(FormatScalar(selector(item)))));
        void Text(string name, Func<OperationalEvent, string?> selector) =>
            columns.Add(new CsvColumn(name, item => CsvValue.Text(NonBlank(selector(item), "未提供"))));
        void Evidence<T>(string name, Func<OperationalEvent, EvidenceValue<T>> selector)
        {
            Scalar(name + ".Availability", item => selector(item).Availability);
            Scalar(name + ".HasValue", item => selector(item).HasValue);
            columns.Add(new CsvColumn(name + ".Value", item =>
            {
                EvidenceValue<T> evidence = selector(item);
                if (!evidence.HasValue)
                {
                    return CsvValue.Scalar($"<{evidence.Availability}>");
                }
                return evidence.Value is string text ? CsvValue.Text(text) : CsvValue.Scalar(FormatScalar(evidence.Value));
            }));
            Text(name + ".Reason", item => selector(item).Reason);
        }
        void Conclusion(string name, Func<OperationalEvent, PhysicalConclusionEvidence> selector)
        {
            Scalar(name + ".Availability", item => selector(item).Availability);
            Scalar(name + ".HasValue", item => selector(item).Code != PhysicalConclusionCode.Unknown);
            Scalar(name + ".Value", item => selector(item).Code);
            Text(name + ".Reason", item => SerializePhysicalConclusionReason(selector(item)));
            Scalar(name + ".Code", item => selector(item).Code);
            Text(name + ".Summary", item => selector(item).Summary);
            Text(name + ".Basis", item => selector(item).Basis);
        }

        Text("EventId", item => item.EventId);
        Text("EventCode", item => item.EventCode);
        Text("First.ActionId", item => string.IsNullOrWhiteSpace(item.FirstActionId) ? "无动作上下文" : item.FirstActionId);
        Text("Latest.ActionId", item => string.IsNullOrWhiteSpace(item.LatestActionId) ? "无动作上下文" : item.LatestActionId);
        Scalar("FirstOccurredAtUtc", item => item.FirstOccurredAtUtc.ToUniversalTime());
        Scalar("LastOccurredAtUtc", item => item.LastOccurredAtUtc.ToUniversalTime());
        Scalar("OccurrenceCount", item => item.OccurrenceCount);
        Scalar("Severity", item => item.Severity);
        Scalar("Category", item => item.Category);
        Text("Title", item => item.Title);
        Text("Scope", item => item.Scope);
        Text("Engine", item => item.Engine);
        Text("Source", item => item.Source);
        Text("DeviceType", item => item.DeviceType);
        Text("DeviceNo", item => item.DeviceNo);
        Text("Station", item => item.Station);
        Text("ActionStage", item => item.ActionStage);
        Conclusion("First.PhysicalConclusion", item => item.FirstPhysicalConclusion);
        Conclusion("Latest.PhysicalConclusion", item => item.LatestPhysicalConclusion);
        Evidence("First.BusinessPaused", item => item.FirstBusinessPaused);
        Evidence("Latest.BusinessPaused", item => item.LatestBusinessPaused);
        Text("First.DetailMessage", item => item.FirstDetailMessage);
        Text("Latest.DetailMessage", item => item.LatestDetailMessage);
        Text("First.Result", item => item.FirstResult);
        Text("Latest.Result", item => item.LatestResult);
        Text("EffectiveGuidance.RequiredActions", item => SerializeEffectiveRequiredActions(item));
        Text("EffectiveGuidance.ForbiddenActions", item => SerializeEffectiveForbiddenActions(item));
        Text("EffectiveGuidance.ContinueCondition", item => ResolveEffectiveContinueCondition(item));

        AddEvidenceColumns(columns, "First", item => item.FirstEvidence);
        AddEvidenceColumns(columns, "Latest", item => item.LatestEvidence);

        return columns.AsReadOnly();
    }

    private static void AddEvidenceColumns(
        List<CsvColumn> columns,
        string prefix,
        Func<OperationalEvent, OperationalEvidence> evidence)
    {
        void ScalarFull(string name, Func<OperationalEvent, object?> selector) =>
            columns.Add(new CsvColumn(name, item => CsvValue.Scalar(FormatScalar(selector(item)))));
        void TextFull(string name, Func<OperationalEvent, string?> selector) =>
            columns.Add(new CsvColumn(name, item => CsvValue.Text(NonBlank(selector(item), "未提供"))));
        void Typed<T>(string name, Func<OperationalEvidence, EvidenceValue<T>> select)
        {
            string fullName = prefix + "." + name;
            ScalarFull(fullName + ".Availability", item => select(evidence(item)).Availability);
            ScalarFull(fullName + ".HasValue", item => select(evidence(item)).HasValue);
            columns.Add(new CsvColumn(fullName + ".Value", item =>
            {
                EvidenceValue<T> value = select(evidence(item));
                if (!value.HasValue)
                {
                    return CsvValue.Scalar($"<{value.Availability}>");
                }
                return value.Value is string externalText
                    ? CsvValue.Text(externalText)
                    : CsvValue.Scalar(FormatScalar(value.Value));
            }));
            TextFull(fullName + ".Reason", item => select(evidence(item)).Reason);
        }
        void Int(string name, Func<OperationalEvidence, EvidenceValue<int>> select) =>
            Typed(name, select);
        void Double(string name, Func<OperationalEvidence, EvidenceValue<double>> select) =>
            Typed(name, select);
        void Bool(string name, Func<OperationalEvidence, EvidenceValue<bool>> select) =>
            Typed(name, select);
        void Date(string name, Func<OperationalEvidence, EvidenceValue<DateTime>> select) =>
            Typed(name, select);
        void String(string name, Func<OperationalEvidence, EvidenceValue<string>> select) =>
            Typed(name, select);
        void Cmd(string name, Func<OperationalEvidence, DeviceCommandEvidence> select)
        {
            string fullName = prefix + "." + name;
            ScalarFull(fullName + ".State", item => select(evidence(item)).State);
            ScalarFull(fullName + ".Availability", item => select(evidence(item)).Availability);
            ScalarFull(fullName + ".HasValue", item =>
                select(evidence(item)).State is not DeviceCommandState.Unknown and not DeviceCommandState.Unavailable);
            ScalarFull(fullName + ".Value", item => select(evidence(item)).State);
            TextFull(fullName + ".Reason", item => select(evidence(item)).Reason);
        }

        String("Workpiece.PlateNo", value => value.Workpiece.PlateNo);
        String("Workpiece.Sequence", value => value.Workpiece.Sequence);
        Double("Workpiece.Diameter", value => value.Workpiece.Diameter);
        Double("Workpiece.Length", value => value.Workpiece.Length);
        String("Workpiece.Source", value => value.Workpiece.Source);
        String("Workpiece.Target", value => value.Workpiece.Target);
        String("Workpiece.SoftwareOwner", value => value.Workpiece.SoftwareOwner);
        String("Workpiece.LastConfirmedLocation", value => value.Workpiece.LastConfirmedLocation);
        TextFull(prefix + ".Workpiece.EvidenceSource", item => evidence(item).Workpiece.EvidenceSource);

        Int("Position.DisplayX", value => value.Position.DisplayX);
        Int("Position.DisplayY", value => value.Position.DisplayY);
        Int("Position.DisplayZ", value => value.Position.DisplayZ);
        Int("Position.AbsX", value => value.Position.AbsX);
        Int("Position.AbsY", value => value.Position.AbsY);
        Int("Position.TargetX", value => value.Position.TargetX);
        Int("Position.TargetY", value => value.Position.TargetY);
        Int("Position.TargetZ", value => value.Position.TargetZ);
        Int("Position.TargetAbsX", value => value.Position.TargetAbsX);
        Int("Position.TargetAbsY", value => value.Position.TargetAbsY);
        Int("Position.LastSentDisplayTargetX", value => value.Position.LastSentDisplayTargetX);
        Int("Position.LastSentDisplayTargetY", value => value.Position.LastSentDisplayTargetY);
        Cmd("Position.XFineTuneCommand", value => value.Position.XFineTuneCommand);
        Cmd("Position.YFineTuneCommand", value => value.Position.YFineTuneCommand);
        String("Position.FailureStage", value => value.Position.FailureStage);
        Int("Position.DeltaX", value => value.Position.DeltaX);
        Int("Position.DeltaY", value => value.Position.DeltaY);
        Int("Position.ToleranceX", value => value.Position.ToleranceX);
        Int("Position.ToleranceY", value => value.Position.ToleranceY);
        Int("Position.MaximumCorrectionX", value => value.Position.MaximumCorrectionX);
        Int("Position.MaximumCorrectionY", value => value.Position.MaximumCorrectionY);
        Int("Position.StableSampleCount", value => value.Position.StableSampleCount);
        Int("Position.StageReadCount", value => value.Position.StageReadCount);
        Int("Position.TotalReadCount", value => value.Position.TotalReadCount);
        Int("Position.FineTuneAttemptCount", value => value.Position.FineTuneAttemptCount);
        Int("Position.FeedbackRereadCount", value => value.Position.FeedbackRereadCount);
        Date("Position.CapturedAtUtc", value => value.Position.CapturedAtUtc);

        Cmd("MotionAndMagnet.ZDownCommand", value => value.MotionAndMagnet.ZDownCommand);
        Bool("MotionAndMagnet.ZMayStillBeLow", value => value.MotionAndMagnet.ZMayStillBeLow);
        Cmd("MotionAndMagnet.MagnetOnCommand", value => value.MotionAndMagnet.MagnetOnCommand);
        Cmd("MotionAndMagnet.MagnetOffCommand", value => value.MotionAndMagnet.MagnetOffCommand);
        Int("MotionAndMagnet.X11LastValue", value => value.MotionAndMagnet.X11LastValue);
        Bool("MotionAndMagnet.X11ReadValid", value => value.MotionAndMagnet.X11ReadValid);
        Int("MotionAndMagnet.X11Attempts", value => value.MotionAndMagnet.X11Attempts);
        Date("MotionAndMagnet.X11ReadAtUtc", value => value.MotionAndMagnet.X11ReadAtUtc);

        ScalarFull(prefix + ".BusinessState.Availability", item => evidence(item).BusinessState.Availability);
        TextFull(prefix + ".BusinessState.Reason", item => evidence(item).BusinessState.Reason);
        String("BusinessState.LastSuccessfulCheckpoint", value => value.BusinessState.LastSuccessfulCheckpoint);
        String("BusinessState.StateBefore", value => value.BusinessState.StateBefore);
        String("BusinessState.StateAfter", value => value.BusinessState.StateAfter);
        String("BusinessState.CacheBefore", value => value.BusinessState.CacheBefore);
        String("BusinessState.CacheAfter", value => value.BusinessState.CacheAfter);
        String("BusinessState.OwnerBefore", value => value.BusinessState.OwnerBefore);
        String("BusinessState.OwnerAfter", value => value.BusinessState.OwnerAfter);
        Bool("BusinessState.HoldingWorkpiece", value => value.BusinessState.HoldingWorkpiece);
        Bool("BusinessState.Placed", value => value.BusinessState.Placed);
        Bool("BusinessState.CacheNotified", value => value.BusinessState.CacheNotified);
        ScalarFull(prefix + ".BusinessState.PhysicalCommitments.Count", item => evidence(item).BusinessState.PhysicalCommitments.Count);
        TextFull(prefix + ".BusinessState.PhysicalCommitments", item => SerializeCommitments(evidence(item).BusinessState.PhysicalCommitments));

        ScalarFull(prefix + ".Locks.Availability", item => evidence(item).Locks.Availability);
        TextFull(prefix + ".Locks.Reason", item => evidence(item).Locks.Reason);
        ScalarFull(prefix + ".Locks.Count", item => evidence(item).Locks.Items.Count);
        TextFull(prefix + ".Locks.Items", item => SerializeLocks(evidence(item).Locks.Items));

        ScalarFull(prefix + ".Recovery.Availability", item => evidence(item).Recovery.Availability);
        TextFull(prefix + ".Recovery.Reason", item => evidence(item).Recovery.Reason);
        Bool("Recovery.Attempted", value => value.Recovery.Attempted);
        Bool("Recovery.Completed", value => value.Recovery.Completed);
        ScalarFull(prefix + ".Recovery.Steps.Count", item => evidence(item).Recovery.Steps.Count);
        TextFull(prefix + ".Recovery.Steps", item => SerializeRecoverySteps(evidence(item).Recovery.Steps));
        ScalarFull(prefix + ".Recovery.PostRecoveryVerification.Count", item => evidence(item).Recovery.PostRecoveryVerification.Count);
        TextFull(prefix + ".Recovery.PostRecoveryVerification", item => SerializeStringArray(evidence(item).Recovery.PostRecoveryVerification));
        TextFull(prefix + ".Recovery.ResultingBusinessBehavior", item => evidence(item).Recovery.ResultingBusinessBehavior);

        ScalarFull(prefix + ".Guidance.Availability", item => evidence(item).Guidance.Availability);
        TextFull(prefix + ".Guidance.Reason", item => evidence(item).Guidance.Reason);
        TextFull(prefix + ".Guidance.RequiredActions", item => JoinOrMarker(evidence(item).Guidance.RequiredActions));
        TextFull(prefix + ".Guidance.ForbiddenActions", item => JoinOrMarker(evidence(item).Guidance.ForbiddenActions));
        TextFull(prefix + ".Guidance.ContinueCondition", item => evidence(item).Guidance.ContinueCondition);

        ScalarFull(prefix + ".Exception.Availability", item => evidence(item).Exception.Availability);
        TextFull(prefix + ".Exception.Reason", item => evidence(item).Exception.Reason);
        TextFull(prefix + ".Exception.Type", item => evidence(item).Exception.Type);
        TextFull(prefix + ".Exception.Message", item => evidence(item).Exception.Message);
        TextFull(prefix + ".Exception.InnerExceptionChain", item => evidence(item).Exception.InnerExceptionChain);
        TextFull(prefix + ".Exception.BusinessContext", item => evidence(item).Exception.BusinessContext);
        TextFull(prefix + ".Exception.StackTrace", item => evidence(item).Exception.StackTrace);
    }

    private static string SerializeCommitments(IReadOnlyList<PhysicalCommitmentEvidence> items) =>
        JsonSerializer.Serialize(items, JsonOptions);

    private static string SerializeLocks(IReadOnlyList<LockItemEvidence> items) =>
        JsonSerializer.Serialize(items, JsonOptions);

    private static string SerializeRecoverySteps(IReadOnlyList<RecoveryStepEvidence> items) =>
        JsonSerializer.Serialize(items, JsonOptions);

    private static string SerializeStringArray(IReadOnlyList<string> items) =>
        JsonSerializer.Serialize(items, JsonOptions);

    private static string SerializePhysicalConclusionReason(PhysicalConclusionEvidence evidence) =>
        JsonSerializer.Serialize(new { evidence.Summary, evidence.Basis }, JsonOptions);

    private static string JoinOrMarker(IReadOnlyList<string> items) =>
        items.Count == 0 ? "<none>" : string.Join(" | ", items);

    private static string SerializeEffectiveRequiredActions(OperationalEvent item)
    {
        OperationalEventDefinition definition = ResolveEffectiveGuidance(item);
        return JoinStable(
            item.LatestEvidence.Guidance.RequiredActions,
            item.FirstEvidence.Guidance.RequiredActions,
            definition.RequiredActions);
    }

    private static string SerializeEffectiveForbiddenActions(OperationalEvent item)
    {
        OperationalEventDefinition definition = ResolveEffectiveGuidance(item);
        return JoinStable(
            item.LatestEvidence.Guidance.ForbiddenActions,
            item.FirstEvidence.Guidance.ForbiddenActions,
            definition.ForbiddenActions);
    }

    private static OperationalEventDefinition ResolveEffectiveGuidance(OperationalEvent item) =>
        OperationalEventCatalog.Default.Resolve(item);

    private static string ResolveEffectiveContinueCondition(OperationalEvent item)
    {
        string[] candidates =
        [
            TrustedContinueCondition(item.LatestEvidence.Guidance),
            TrustedContinueCondition(item.FirstEvidence.Guidance),
            ResolveEffectiveGuidance(item).ContinueCondition
        ];
        return candidates.First(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string TrustedContinueCondition(OperatorGuidance guidance) =>
        guidance.Availability is EvidenceAvailability.Confirmed or EvidenceAvailability.Inferred
            ? guidance.ContinueCondition
            : string.Empty;

    private static string JoinStable(params IEnumerable<string>[] groups)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<string>();
        foreach (IEnumerable<string> group in groups)
        {
            foreach (string? raw in group ?? [])
            {
                string value = raw?.Trim() ?? string.Empty;
                if (value.Length > 0 && seen.Add(value))
                {
                    values.Add(value);
                }
            }
        }
        return values.Count == 0 ? "<none>" : string.Join(" | ", values);
    }

    private static string FormatScalar(object? value) => value switch
    {
        null => "<null>",
        DateTime date => FormatDate(date),
        DateTimeOffset offset => offset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "<empty>",
        _ => value.ToString() ?? "<empty>"
    };

    private static string FormatDate(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string NonBlank(string? value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record CsvColumn(string Name, Func<OperationalEvent, CsvValue> Select);

    private readonly record struct CsvValue(string Value, bool IsExternalText)
    {
        public static CsvValue Scalar(string value) => new(value, false);

        public static CsvValue Text(string value) => new(value, true);
    }
}
