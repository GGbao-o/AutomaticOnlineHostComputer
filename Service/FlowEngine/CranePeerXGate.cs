using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 前后天车持锁等待对方实际X的只读准入组件。调用方先取得区域锁，再进入本组件；
/// 位置不满足时保留锁和工件任务，每500ms再读，直到人工/原流程移动对方天车后条件满足。
/// 本组件不获取或释放任何调度锁，不发运动、充磁、放磁命令，也不改变暂停或工件状态。
/// </summary>
internal static class CranePeerXGate
{
    /// <summary>
    /// 真实设备入口：复用对方天车连接，实时读取D5018/D5019解码后的XPos。
    /// 阈值用委托逐轮读取，配置页修改不必结束本趟任务；实际X不叠加坐标/退避偏移。
    /// 后端传入租约校验，保证应急失效后不再继续本动作；通信失败交原catch/finally收尾。
    /// </summary>
    internal static Task WaitAsync(CraneService peer, int currentCraneNo, int peerCraneNo,
        Func<int> readThreshold, bool requireAtLeast, CancellationToken ct, Action? validateAction = null)
        => WaitAsync(currentCraneNo, peerCraneNo, async token =>
        {
            validateAction?.Invoke();
            if (!peer.IsConnected)
                await peer.ConnectAsync(token);
            var status = await peer.ReadStatusAsync(token);
            validateAction?.Invoke();
            return status?.XPos; // 读不到返回null，绝不能伪装成X=0或使用上次值放行。
        }, readThreshold, requireAtLeast, (ms, token) => Task.Delay(ms, token), Console.WriteLine, ct);

    /// <summary>
    /// 可注入IO的等待核心，供无PLC测试验证方向、边界、实时配置与取消。
    /// 没有业务超时或重试次数上限；停止/取消仍由原令牌终止，锁由调用方原finally释放。
    /// </summary>
    internal static async Task WaitAsync(int currentCraneNo, int peerCraneNo,
        Func<CancellationToken, Task<int?>> readX, Func<int> readThreshold, bool requireAtLeast,
        Func<int, CancellationToken, Task> delay, Action<string> log, CancellationToken ct)
    {
        string comparison = requireAtLeast ? ">=" : "<=";
        int heartbeat = 0;
        bool waiting = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int? actualX = await readX(ct);
            ct.ThrowIfCancellationRequested();
            if (!actualX.HasValue)
                throw new InvalidOperationException($"{currentCraneNo}号天车持锁X准入失败：{peerCraneNo}号天车实际X读取失败，禁止继续运动");

            int threshold = readThreshold();
            if (requireAtLeast ? actualX.Value >= threshold : actualX.Value <= threshold)
            {
                log($"[天车X准入] {currentCraneNo}号读取{peerCraneNo}号：实际X={actualX.Value}mm，条件X{comparison}{threshold}mm，已满足，继续本趟任务");
                return;
            }

            // 首次及约10秒一次输出状态；不因位置变化每500ms刷日志。无限等待不清工件、不退磁。
            if (!waiting || ++heartbeat >= 20)
            {
                log($"[天车X准入] {currentCraneNo}号读取{peerCraneNo}号：实际X={actualX.Value}mm，条件X{comparison}{threshold}mm，未满足，保持区域锁无限等待");
                heartbeat = 0;
                waiting = true;
            }
            await delay(500, ct);
        }
    }
}
