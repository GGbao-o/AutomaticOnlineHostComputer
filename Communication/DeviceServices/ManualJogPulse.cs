using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices;

/// <summary>仅表示手动JOG写0/回读未确认；不暂停线路或改动任何生产状态。</summary>
internal sealed class ManualJogResetException : Exception
{
    internal ManualJogResetException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>固定同一设备和线圈的IO适配；测试可注入失败/取消，不连接真实PLC。</summary>
internal sealed record ManualJogOperations(
    Func<bool, CancellationToken, Task> Write,
    Func<CancellationToken, Task<bool>> Read,
    Func<int, CancellationToken, Task> Delay);

/// <summary>
/// 手动定时JOG：写1一次、等待点击时的时长、finally独立写0并回读。
/// 只处理本次手动线圈，不接触生产引擎、业务锁、队列或工件；断电/强杀不在本组件保障范围。
/// </summary>
internal static class ManualJogPulse
{
    internal static void Validate(int coilAddress, int durationMs)
    {
        ValidateCoil(coilAddress);
        if (durationMs <= 0) throw new ArgumentOutOfRangeException(nameof(durationMs), "JOG持续时间必须大于0ms");
    }

    private static void ValidateCoil(int coilAddress)
    {
        if (coilAddress is < 70 or > 75)
            throw new ArgumentOutOfRangeException(nameof(coilAddress), "手动JOG只允许M70～M75");
    }

    /// <summary>
    /// 核心方法
    /// </summary>
    /// <param name="io"></param>
    /// <param name="coilAddress"></param>
    /// <param name="durationMs"></param>
    /// <param name="ct"></param>
    /// <exception cref="ManualJogResetException"></exception>
    internal static async Task RunAsync(ManualJogOperations io, int coilAddress, int durationMs, CancellationToken ct)
    {
        Validate(coilAddress, durationMs);
        ct.ThrowIfCancellationRequested();
        Exception? originalError = null;
        // 必须先建立finally再尝试写1。写1即使超时/取消，PLC也可能已经执行，不能省略写0。
        try
        {
            await io.Write(true, ct);
            await io.Delay(durationMs, ct);
        }
        catch (Exception ex)
        {
            originalError = ex;
            throw;
        }
        finally
        {
            try { await ResetAsync(io, coilAddress); }
            catch (ManualJogResetException resetError)
            {
                // 复位失败优先呈现；同时保留写1/等待阶段原异常，不能被finally覆盖后丢失证据。
                throw new ManualJogResetException(resetError.Message,
                    originalError == null ? resetError : new AggregateException(originalError, resetError));
            }
        }
    }

    /// <summary>
    /// 复位不使用原取消令牌：每次独立5秒，最多3次写0+FC01回读。
    /// 写0成功但读到1/读失败均不算完成；只重发幂等OFF，绝不重发ON。
    /// </summary>
    internal static async Task ResetAsync(ManualJogOperations io, int coilAddress)
    {
        ValidateCoil(coilAddress);
        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await io.Write(false, cleanupCts.Token);
                if (await io.Read(cleanupCts.Token))
                    throw new InvalidOperationException($"M{coilAddress}写0后仍回读为1");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                // 日志输出异常也不能打断后续OFF重试。
                try { Console.WriteLine($"[手动JOG] M{coilAddress}复位未确认({attempt}/3)：{ex.Message}"); }
                catch { /* 继续写0优先于日志。 */ }
            }
        }
        throw new ManualJogResetException($"M{coilAddress}写0并回读3次仍未确认；禁止开始新的JOG，请重试原线圈复位", last);
    }
}
