using System.IO;
using System.Text.Json;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Infrastructure.Config;

public static class OperationalEventMonitorOptionsLoader
{
    private const string DefaultRelativePath = "Config/operational_event_monitor_settings.json";

    public static OperationalEventMonitorOptions Load(string? path = null)
    {
        try
        {
            string resolvedPath = string.IsNullOrWhiteSpace(path)
                ? Path.Combine(AppContext.BaseDirectory, DefaultRelativePath)
                : path;

            if (!File.Exists(resolvedPath))
            {
                return OperationalEventMonitorOptions.Default;
            }

            string json = File.ReadAllText(resolvedPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return OperationalEventMonitorOptions.Default;
            }

            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is JsonValueKind.Null or not JsonValueKind.Object)
            {
                return OperationalEventMonitorOptions.Default;
            }

            return Merge(document.RootElement);
        }
        catch
        {
            return OperationalEventMonitorOptions.Default;
        }
    }

    private static OperationalEventMonitorOptions Merge(JsonElement root)
    {
        OperationalEventMonitorOptions defaults = OperationalEventMonitorOptions.Default;

        int capacity = ReadPositiveInt(root, "capacity") is int configuredCapacity
            ? Math.Min(configuredCapacity, OperationalEventMonitorOptions.MaximumCapacity)
            : defaults.Capacity;

        TimeSpan aggregationWindow = ReadPositiveSeconds(root, "aggregationWindowSeconds")
            ?? defaults.AggregationWindow;
        int transientCount = ReadPositiveInt(root, "transientFailureCountThreshold")
            ?? defaults.TransientFailureCountThreshold;
        TimeSpan transientDuration = ReadPositiveSeconds(root, "transientFailureDurationSeconds")
            ?? defaults.TransientFailureDuration;

        var stageThresholds = new Dictionary<string, TimeSpan>(
            OperationalEventMonitorOptions.CopyDefaultStageThresholds(),
            StringComparer.OrdinalIgnoreCase);

        if (TryGetProperty(root, "stageThresholdSeconds", out JsonElement stages) &&
            stages.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty stage in stages.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(stage.Name) ||
                    !TryReadPositiveSeconds(stage.Value, out TimeSpan threshold))
                {
                    continue;
                }

                stageThresholds[stage.Name] = threshold;
            }
        }

        return OperationalEventMonitorOptions.Create(
            capacity,
            aggregationWindow,
            transientCount,
            transientDuration,
            stageThresholds);
    }

    private static int? ReadPositiveInt(JsonElement root, string propertyName)
    {
        return TryGetProperty(root, propertyName, out JsonElement value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out int parsed) &&
               parsed > 0
            ? parsed
            : null;
    }

    private static TimeSpan? ReadPositiveSeconds(JsonElement root, string propertyName)
    {
        return TryGetProperty(root, propertyName, out JsonElement value) &&
               TryReadPositiveSeconds(value, out TimeSpan parsed)
            ? parsed
            : null;
    }

    private static bool TryReadPositiveSeconds(JsonElement value, out TimeSpan result)
    {
        result = default;
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int seconds) ||
            seconds <= 0)
        {
            return false;
        }

        result = TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
