using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AutomaticOnlineHostComputer.Tests")]

namespace AutomaticOnlineHostComputer.Service.OperationalEvents;

public sealed class OperationalEventMonitorOptions
{
    public const int MaximumCapacity = 5000;

    private const int DefaultTransientFailureCountThreshold = 3;
    private static readonly TimeSpan DefaultAggregationWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultTransientFailureDuration = TimeSpan.FromSeconds(30);

    private static readonly IReadOnlyDictionary<string, TimeSpan> DefaultStageThresholds =
        FreezeStageThresholds(new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase)
        {
            ["SOFTWARE_LOCK"] = TimeSpan.FromSeconds(60),
            ["ZONE_MT"] = TimeSpan.FromSeconds(60),
            ["ZONE_TS"] = TimeSpan.FromSeconds(60),
            ["POSITION_LOCK"] = TimeSpan.FromSeconds(60),
            ["R6101"] = TimeSpan.FromMinutes(10),
            ["R6103"] = TimeSpan.FromMinutes(10),
            ["R6107"] = TimeSpan.FromMinutes(90),
            ["FORK_RETURN"] = TimeSpan.FromMinutes(5),
            ["CRANE_RETURN"] = TimeSpan.FromMinutes(5),
            ["TRANSFER_RACK_FULL"] = TimeSpan.FromMinutes(10),
            ["MARKER_B_FILE"] = TimeSpan.FromMinutes(30),
            ["DEFAULT_NO_BUSINESS_TIMEOUT"] = TimeSpan.FromMinutes(10)
        });

    private OperationalEventMonitorOptions(
        int capacity,
        TimeSpan aggregationWindow,
        int transientFailureCountThreshold,
        TimeSpan transientFailureDuration,
        IReadOnlyDictionary<string, TimeSpan> stageThresholds)
    {
        Capacity = capacity;
        AggregationWindow = aggregationWindow;
        TransientFailureCountThreshold = transientFailureCountThreshold;
        TransientFailureDuration = transientFailureDuration;
        StageThresholds = FreezeStageThresholds(stageThresholds);
    }

    public int Capacity { get; }

    public TimeSpan AggregationWindow { get; }

    public int TransientFailureCountThreshold { get; }

    public TimeSpan TransientFailureDuration { get; }

    public IReadOnlyDictionary<string, TimeSpan> StageThresholds { get; }

    public static OperationalEventMonitorOptions Default => Create(
        MaximumCapacity,
        DefaultAggregationWindow,
        DefaultTransientFailureCountThreshold,
        DefaultTransientFailureDuration,
        DefaultStageThresholds);

    public TimeSpan GetStageThreshold(string? stageCode)
    {
        if (!string.IsNullOrWhiteSpace(stageCode) &&
            StageThresholds.TryGetValue(stageCode, out TimeSpan threshold))
        {
            return threshold;
        }

        return StageThresholds["DEFAULT_NO_BUSINESS_TIMEOUT"];
    }

    internal static OperationalEventMonitorOptions Create(
        int capacity,
        TimeSpan aggregationWindow,
        int transientFailureCountThreshold,
        TimeSpan transientFailureDuration,
        IReadOnlyDictionary<string, TimeSpan>? stageThresholds)
    {
        int validatedCapacity = capacity <= 0
            ? MaximumCapacity
            : Math.Min(capacity, MaximumCapacity);
        TimeSpan validatedAggregationWindow = aggregationWindow > TimeSpan.Zero
            ? aggregationWindow
            : DefaultAggregationWindow;
        int validatedTransientCount = transientFailureCountThreshold > 0
            ? transientFailureCountThreshold
            : DefaultTransientFailureCountThreshold;
        TimeSpan validatedTransientDuration = transientFailureDuration > TimeSpan.Zero
            ? transientFailureDuration
            : DefaultTransientFailureDuration;

        var mergedStageThresholds = new Dictionary<string, TimeSpan>(
            DefaultStageThresholds,
            StringComparer.OrdinalIgnoreCase);
        if (stageThresholds is not null)
        {
            foreach ((string stageCode, TimeSpan threshold) in stageThresholds)
            {
                if (string.IsNullOrWhiteSpace(stageCode) || threshold <= TimeSpan.Zero)
                {
                    continue;
                }

                mergedStageThresholds[stageCode] = threshold;
            }
        }

        return new OperationalEventMonitorOptions(
            validatedCapacity,
            validatedAggregationWindow,
            validatedTransientCount,
            validatedTransientDuration,
            mergedStageThresholds);
    }

    internal static IReadOnlyDictionary<string, TimeSpan> CopyDefaultStageThresholds()
    {
        return FreezeStageThresholds(DefaultStageThresholds);
    }

    private static IReadOnlyDictionary<string, TimeSpan> FreezeStageThresholds(
        IEnumerable<KeyValuePair<string, TimeSpan>> source)
    {
        var copy = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, TimeSpan value) in source)
        {
            copy[key] = value;
        }

        return new ReadOnlyDictionary<string, TimeSpan>(copy);
    }
}
