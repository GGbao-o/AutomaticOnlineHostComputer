using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>验证实际Y的严格边界和D5007严格==1；缺值、非1不能靠低Y绕过。</summary>
public sealed class ManipulatorRackPositionGateTests
{
    [Theory]
    [InlineData(0, 1, 2000, true)]
    [InlineData(1999, 1, 2000, true)]
    [InlineData(2000, 1, 2000, false)]
    [InlineData(2001, 1, 2000, false)]
    [InlineData(0, 0, 2000, false)]
    [InlineData(0, 2, 2000, false)]
    [InlineData(0, -1, 2000, false)]
    [InlineData(0, 32767, 2000, false)]
    [InlineData(0, null, 2000, false)]
    [InlineData(null, 1, 2000, false)]
    [InlineData(null, null, 2000, false)]
    [InlineData(0, 1, 0, false)]
    [InlineData(0, 1, 1, true)]
    public void Only_a_known_y_below_the_configured_threshold_and_exact_home_flag_one_pass(
        int? actualY, int? yHomeCompleted, int belowY, bool expected)
        => Assert.Equal(expected, ManipulatorRackPositionGate.IsClear(actualY, (short?)yHomeCompleted, belowY));
}
