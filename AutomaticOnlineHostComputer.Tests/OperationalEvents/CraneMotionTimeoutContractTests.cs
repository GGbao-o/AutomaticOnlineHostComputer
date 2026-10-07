namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class CraneMotionTimeoutContractTests
{
    [Fact]
    public void Crane_motion_timeout_contract_has_final_position_axis_and_stop_evidence()
    {
        string source = Read("Communication", "DeviceServices", "CraneService.cs");

        Assert.Contains("public sealed class CraneMotionTimeoutException", source, StringComparison.Ordinal);
        Assert.Contains("LastKnownStatus", source, StringComparison.Ordinal);
        Assert.Contains("UnreachedAxes", source, StringComparison.Ordinal);
        Assert.Contains("TryTriggerManualEmergencyStopAsync", source, StringComparison.Ordinal);
        Assert.Contains("CreateMotionTimeoutExceptionAsync", source, StringComparison.Ordinal);
        Assert.Contains("Addr.D_ManualEStop", source, StringComparison.Ordinal);
        Assert.Contains("XY先行", source, StringComparison.Ordinal);
        Assert.Contains("Z下降", source, StringComparison.Ordinal);
        Assert.Contains("X2下压停止后Z正常到位", source, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(200, emergencyCt)", source, StringComparison.Ordinal);
        Assert.Contains("PressureStopRecoveryPolicy.IsNormalArrival", source, StringComparison.Ordinal);
        Assert.Contains("await RecoverFromPressureStopAsync(emergencyCt)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Crane_cache_injects_configured_xy_and_z_timeouts_but_manipulator_cache_does_not()
    {
        string craneCache = Read("Communication", "DeviceServices", "CraneConnectionCache.cs");
        string manipulatorCache = Read("Communication", "DeviceServices", "ManipulatorConnectionCache.cs");
        string home = Read("Presentation", "ViewModels", "Home", "HomeViewModel.cs");

        Assert.Contains("new CraneConnectionCache(_cfg)", home, StringComparison.Ordinal);
        Assert.Contains("xyTimeoutMs: motion.XyTimeoutMs", craneCache, StringComparison.Ordinal);
        Assert.Contains("zTimeoutMs: motion.ZTimeoutMs", craneCache, StringComparison.Ordinal);
        Assert.DoesNotContain("xyTimeoutMs:", manipulatorCache, StringComparison.Ordinal);
    }

    [Fact]
    public void Motion_configuration_sets_default_crane_xy_and_z_timeouts_to_120_seconds()
    {
        string model = Read("Infrastructure", "Config", "MotionConfig.cs");
        string json = Read("Config", "motion_settings.json");
        string fineTune = Read("Service", "FlowEngine", "XAbsFineTuneHelper.cs");

        Assert.Contains("XyTimeoutMs { get; set; } = 120_000", model, StringComparison.Ordinal);
        Assert.Contains("ZTimeoutMs { get; set; } = 120_000", model, StringComparison.Ordinal);
        Assert.Contains("\"xyTimeoutMs\": 120000", json, StringComparison.Ordinal);
        Assert.Contains("\"zTimeoutMs\": 120000", json, StringComparison.Ordinal);
        Assert.Contains("cfg.GetCraneSpeed(craneNo).XyTimeoutMs", fineTune, StringComparison.Ordinal);
    }

    [Fact]
    public void Rear_crane_timeout_releases_software_locks_and_keeps_workpiece_state_for_manual_confirmation()
    {
        string lease = Read("Service", "FlowEngine", "EmergencyActionLease.cs");
        string line1Rear = Read("Service", "FlowEngine", "Line1RearFlowEngine.cs");
        string line2Rear = Read("Service", "FlowEngine", "Line2RearFlowEngine.cs");

        Assert.Contains("HoldForManualSafetyRecovery", lease, StringComparison.Ordinal);
        Assert.Contains("RequiresManualSafetyRecovery", lease, StringComparison.Ordinal);
        foreach (string source in new[] { line1Rear, line2Rear })
        {
            Assert.Contains("catch (CraneMotionTimeoutException ex)", source, StringComparison.Ordinal);
            Assert.Contains("requiresManualConfirmation = true", source, StringComparison.Ordinal);
            Assert.Contains("_rackLedger.ReleaseReservation(rs, actionId)", source, StringComparison.Ordinal);
            Assert.Contains("本动作软件锁将在收尾后释放", source, StringComparison.Ordinal);
            Assert.Contains("if (operation.RequiresManualSafetyRecovery)", source, StringComparison.Ordinal);
            Assert.Contains("operation.TryRelease(\"RearCrane\"", source, StringComparison.Ordinal);
        }
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([RepositoryRoot.Find(), .. parts]));
}
