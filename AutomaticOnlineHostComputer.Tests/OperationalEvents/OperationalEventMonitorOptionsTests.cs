using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class OperationalEventMonitorOptionsTests
{
    [Fact]
    public void Invalid_monitor_config_falls_back_without_throwing()
    {
        string path = WriteTemporaryFile("{ invalid json");

        OperationalEventMonitorOptions options = OperationalEventMonitorOptionsLoader.Load(path);

        AssertDefaults(options);
    }

    [Fact]
    public void Missing_empty_and_json_null_configs_fall_back()
    {
        string directory = CreateTemporaryDirectory();
        string missingPath = Path.Combine(directory, "missing.json");
        string emptyPath = WriteTemporaryFile(string.Empty);
        string nullPath = WriteTemporaryFile("null");

        AssertDefaults(OperationalEventMonitorOptionsLoader.Load(missingPath));
        AssertDefaults(OperationalEventMonitorOptionsLoader.Load(emptyPath));
        AssertDefaults(OperationalEventMonitorOptionsLoader.Load(nullPath));
    }

    [Fact]
    public void Partial_config_merges_with_defaults_and_ignores_unknown_keys()
    {
        string path = WriteTemporaryFile("""
            {
              "aggregationWindowSeconds": 120,
              "unknownTopLevel": 123,
              "stageThresholdSeconds": {
                "R6101": 15,
                "CUSTOM_STAGE": 45
              }
            }
            """);

        OperationalEventMonitorOptions options = OperationalEventMonitorOptionsLoader.Load(path);

        Assert.Equal(TimeSpan.FromSeconds(120), options.AggregationWindow);
        Assert.Equal(TimeSpan.FromSeconds(15), options.StageThresholds["R6101"]);
        Assert.Equal(TimeSpan.FromSeconds(45), options.StageThresholds["CUSTOM_STAGE"]);
        Assert.Equal(TimeSpan.FromSeconds(600), options.StageThresholds["R6103"]);
        Assert.Equal(5000, options.Capacity);
    }

    [Fact]
    public void Non_positive_values_fall_back_per_item_and_capacity_is_hard_capped()
    {
        string path = WriteTemporaryFile("""
            {
              "capacity": 99999,
              "aggregationWindowSeconds": 0,
              "transientFailureCountThreshold": -2,
              "transientFailureDurationSeconds": 0,
              "stageThresholdSeconds": {
                "R6101": -1,
                "R6103": 25
              }
            }
            """);

        OperationalEventMonitorOptions options = OperationalEventMonitorOptionsLoader.Load(path);

        Assert.Equal(5000, options.Capacity);
        Assert.Equal(TimeSpan.FromMinutes(10), options.AggregationWindow);
        Assert.Equal(3, options.TransientFailureCountThreshold);
        Assert.Equal(TimeSpan.FromSeconds(30), options.TransientFailureDuration);
        Assert.Equal(TimeSpan.FromMinutes(10), options.StageThresholds["R6101"]);
        Assert.Equal(TimeSpan.FromSeconds(25), options.StageThresholds["R6103"]);
    }

    [Fact]
    public void Type_error_in_any_setting_falls_back_without_throwing()
    {
        string path = WriteTemporaryFile("""
            {
              "capacity": "not-a-number",
              "aggregationWindowSeconds": 42,
              "stageThresholdSeconds": []
            }
            """);

        OperationalEventMonitorOptions options = OperationalEventMonitorOptionsLoader.Load(path);

        Assert.Equal(5000, options.Capacity);
        Assert.Equal(TimeSpan.FromSeconds(42), options.AggregationWindow);
        Assert.Equal(TimeSpan.FromMinutes(10), options.StageThresholds["R6101"]);
    }

    [Fact]
    public void Stage_codes_are_case_insensitive_and_dictionary_is_defensively_copied()
    {
        string path = WriteTemporaryFile("""
            {
              "stageThresholdSeconds": {
                "r6101": 17
              }
            }
            """);

        OperationalEventMonitorOptions options = OperationalEventMonitorOptionsLoader.Load(path);

        Assert.Equal(TimeSpan.FromSeconds(17), options.StageThresholds["R6101"]);
        Assert.Equal(TimeSpan.FromSeconds(17), options.GetStageThreshold("r6101"));
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, TimeSpan>>(options.StageThresholds);
        Assert.False(options.StageThresholds is Dictionary<string, TimeSpan>);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, TimeSpan>)options.StageThresholds).Clear());
    }

    [Fact]
    public void Built_in_defaults_include_every_approved_stage_threshold()
    {
        OperationalEventMonitorOptions options = OperationalEventMonitorOptions.Default;

        Assert.Equal(TimeSpan.FromSeconds(60), options.StageThresholds["SOFTWARE_LOCK"]);
        Assert.Equal(TimeSpan.FromSeconds(60), options.StageThresholds["ZONE_MT"]);
        Assert.Equal(TimeSpan.FromSeconds(60), options.StageThresholds["ZONE_TS"]);
        Assert.Equal(TimeSpan.FromSeconds(60), options.StageThresholds["POSITION_LOCK"]);
        Assert.Equal(TimeSpan.FromMinutes(10), options.StageThresholds["R6101"]);
        Assert.Equal(TimeSpan.FromMinutes(10), options.StageThresholds["R6103"]);
        Assert.Equal(TimeSpan.FromMinutes(90), options.StageThresholds["R6107"]);
        Assert.Equal(TimeSpan.FromMinutes(5), options.StageThresholds["FORK_RETURN"]);
        Assert.Equal(TimeSpan.FromMinutes(5), options.StageThresholds["CRANE_RETURN"]);
        Assert.Equal(TimeSpan.FromMinutes(10), options.StageThresholds["TRANSFER_RACK_FULL"]);
        Assert.Equal(TimeSpan.FromMinutes(30), options.StageThresholds["MARKER_B_FILE"]);
        Assert.Equal(TimeSpan.FromMinutes(10), options.StageThresholds["DEFAULT_NO_BUSINESS_TIMEOUT"]);
    }

    private static void AssertDefaults(OperationalEventMonitorOptions options)
    {
        Assert.Equal(5000, options.Capacity);
        Assert.Equal(TimeSpan.FromMinutes(10), options.AggregationWindow);
        Assert.Equal(3, options.TransientFailureCountThreshold);
        Assert.Equal(TimeSpan.FromSeconds(30), options.TransientFailureDuration);
        Assert.Equal(TimeSpan.FromMinutes(90), options.StageThresholds["R6107"]);
    }

    private static string WriteTemporaryFile(string content)
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "operational_event_monitor_settings.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OperationalEventTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
