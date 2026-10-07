using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 后天车从斜床派发下料前的机械手2/3报警门禁。
/// 只决定能否创建新任务；不暂停引擎、不修改工件或斜床状态，也不取得任何动作锁。
/// </summary>
internal static class ManipulatorAlarmDispatchGate
{
    /// <summary>
    /// 两台机械手的D5011、D5012、D5013均为0才允许派发。
    /// 状态读取失败不能按“无报警”处理，保守地拒绝本轮候选。
    /// </summary>
    public static async Task<bool> CanDispatchRearSkewUnloadAsync(
        ManipulatorConnectionCache manipulatorCache,
        string lineName,
        CancellationToken ct)
    {
        try
        {
            var manipulator2 = manipulatorCache.GetOrCreateService(2);
            var manipulator3 = manipulatorCache.GetOrCreateService(3);
            var status2 = await manipulator2.ReadStatusAsync(ct);
            var status3 = await manipulator3.ReadStatusAsync(ct);

            if (status2 == null || status3 == null)
            {
                Console.WriteLine($"│   [下料候选] {lineName}机械手2/3状态读取失败，禁止按无报警派发下料");
                return false;
            }

            bool manipulator2AlarmFree = status2.ServoAlarm == 0
                && status2.PlcServoAlarm == 0
                && status2.PlcAlarm == 0;
            bool manipulator3AlarmFree = status3.ServoAlarm == 0
                && status3.PlcServoAlarm == 0
                && status3.PlcAlarm == 0;
            if (manipulator2AlarmFree && manipulator3AlarmFree)
                return true;

            Console.WriteLine(
                $"│   [下料候选] {lineName}机械手报警，禁止派发下料: " +
                $"M2(D5011={status2.ServoAlarm},D5012={status2.PlcServoAlarm},D5013={status2.PlcAlarm}) " +
                $"M3(D5011={status3.ServoAlarm},D5012={status3.PlcServoAlarm},D5013={status3.PlcAlarm})");
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"│   [下料候选] {lineName}读取机械手2/3报警异常，禁止派发下料: {ex.Message}");
            return false;
        }
    }
}
