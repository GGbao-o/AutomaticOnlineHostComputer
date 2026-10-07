using System;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices;

/// <summary>
/// X2 下压触发后的正常接触判定。只比较已经停止后的实际 Z 与本次 Z 目标，
/// 不参与任何 PLC 写入或引擎暂停决策。
/// </summary>
public static class PressureStopRecoveryPolicy
{
    public static bool IsNormalArrival(int actualZ, int targetZ, int toleranceMm)
    {
        if (toleranceMm < 0)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm), toleranceMm, "下压正常到位阈值不能小于 0mm");

        return Math.Abs((long)actualZ - targetZ) <= toleranceMm;
    }
}
