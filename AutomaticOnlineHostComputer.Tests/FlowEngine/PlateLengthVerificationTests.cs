using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>
/// <see cref="PlateLengthVerification"/> 的纯逻辑单元测试。
/// 容差边界（正好等于容差算通过）与"没有比对基准"的两种情形都由这里钉死。
/// </summary>
public sealed class PlateLengthVerificationTests
{
    [Theory]
    [InlineData(900, 900d, 15, PlateLengthVerdict.Pass)]      // 完全相等
    [InlineData(900, 915d, 15, PlateLengthVerdict.Pass)]      // 正好 +15 → 通过（含边界）
    [InlineData(900, 885d, 15, PlateLengthVerdict.Pass)]      // 正好 -15 → 通过（含边界）
    [InlineData(900, 916d, 15, PlateLengthVerdict.Mismatch)]  // +16 → 拒绝
    [InlineData(900, 884d, 15, PlateLengthVerdict.Mismatch)]  // -16 → 拒绝
    [InlineData(900, 914.9d, 15, PlateLengthVerdict.Pass)]    // 任务板长带小数也直接比较
    [InlineData(900, 915.1d, 15, PlateLengthVerdict.Mismatch)]
    [InlineData(900, 900d, 0, PlateLengthVerdict.Pass)]       // 容差 0 且完全相等
    [InlineData(900, 901d, 0, PlateLengthVerdict.Mismatch)]   // 容差 0 且相差 1
    public void Evaluate_applies_inclusive_plate_length_tolerance(
        int measuredMm, double taskLengthMm, int toleranceMm, PlateLengthVerdict expected)
    {
        Assert.Equal(expected, PlateLengthVerification.Evaluate(measuredMm, taskLengthMm, toleranceMm));
    }

    [Theory]
    [InlineData(0, 900d)]    // D100 读到 0：MC 短报文，没有可用测量值
    [InlineData(-1, 900d)]
    [InlineData(900, 0d)]    // 任务板长无效：没有比对基准
    [InlineData(900, -5d)]
    [InlineData(0, 0d)]
    public void Evaluate_treats_a_missing_reference_as_not_comparable(int measuredMm, double taskLengthMm)
    {
        Assert.Equal(
            PlateLengthVerdict.NotComparable,
            PlateLengthVerification.Evaluate(measuredMm, taskLengthMm, 15));
    }

    [Fact]
    public void Evaluate_clamps_a_negative_tolerance_to_zero()
    {
        Assert.Equal(PlateLengthVerdict.Mismatch, PlateLengthVerification.Evaluate(900, 901d, -15));
        Assert.Equal(PlateLengthVerdict.Pass, PlateLengthVerification.Evaluate(900, 900d, -15));
    }

    [Fact]
    public void Motion_config_defaults_the_plate_length_tolerance_to_15mm()
    {
        Assert.Equal(15, new MotionConfig().Safety.PlateLengthToleranceMm);
    }

    [Fact]
    public void Mismatch_exception_carries_measured_expected_tolerance_and_deviation()
    {
        var exception = new PlateLengthMismatchException(measuredMm: 884, expectedMm: 900d, toleranceMm: 15);

        Assert.Equal(884, exception.MeasuredMm);
        Assert.Equal(900d, exception.ExpectedMm, 6);
        Assert.Equal(15, exception.ToleranceMm);
        Assert.Equal(16d, exception.DeviationMm, 6);
        Assert.Contains("884", exception.Message, StringComparison.Ordinal);
        Assert.Contains("900", exception.Message, StringComparison.Ordinal);
    }
}
