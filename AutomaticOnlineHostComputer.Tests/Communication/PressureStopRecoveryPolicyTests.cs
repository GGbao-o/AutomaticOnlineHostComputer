using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Infrastructure.Config;

namespace AutomaticOnlineHostComputer.Tests.Communication;

public sealed class PressureStopRecoveryPolicyTests
{
    [Theory]
    [InlineData(1000, 1015, 15, true)]
    [InlineData(1000, 985, 15, true)]
    [InlineData(1000, 1016, 15, false)]
    [InlineData(1000, 984, 15, false)]
    public void Stopped_position_uses_the_configured_inclusive_normal_arrival_tolerance(
        int actualZ, int targetZ, int toleranceMm, bool expected)
    {
        Assert.Equal(expected, PressureStopRecoveryPolicy.IsNormalArrival(actualZ, targetZ, toleranceMm));
    }

    [Fact]
    public void Motion_config_defaults_pressure_stop_normal_arrival_tolerance_to_15mm()
    {
        Assert.Equal(15, new MotionConfig().Safety.PressureStopNormalPositionToleranceMm);
    }

    [Fact]
    public void Negative_pressure_stop_normal_arrival_tolerance_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PressureStopRecoveryPolicy.IsNormalArrival(1000, 1000, -1));
    }
}
