using System;
using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 一次搬运动作在最终异常处只能有一个安全结论。
/// <para>尚未发送物理命令时允许等待下一轮；一旦已发送命令，响应未知不能以断线重试的名义回放。</para>
/// <para>本类只更新动作账本，不发送设备命令、不释放锁、不修改缓存；各引擎据结论执行自己的暂停和告警收尾。</para>
/// </summary>
internal enum FlowActionExceptionConclusion
{
    WaitRetry,
    PauseLineManual
}

internal static class FlowActionExceptionResolver
{
    public static FlowActionExceptionConclusion Resolve(FlowActionContext action, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(exception);

        if (action.ManualConfirmationRequired)
            return FlowActionExceptionConclusion.PauseLineManual;

        // 正常完成后不应再落入最终 catch；保守地禁止把已完成动作重新入队。
        if (action.IsFinalized)
            return FlowActionExceptionConclusion.PauseLineManual;

        // 三次磁铁反馈仍未确认时，哪怕触发写入是否真正到达PLC未知，也不能继续自动流程。
        if (exception is MagnetFeedbackTimeoutException)
        {
            action.MarkCommandResponseUnknown(exception.Message);
            action.PauseForManualResolution(exception.Message);
            return FlowActionExceptionConclusion.PauseLineManual;
        }

        if (action.CommandState == FlowCommandState.NotSent)
        {
            action.WaitRetry(exception.Message);
            return FlowActionExceptionConclusion.WaitRetry;
        }

        action.MarkCommandResponseUnknown(exception.Message);
        action.PauseForManualResolution(exception.Message);
        return FlowActionExceptionConclusion.PauseLineManual;
    }
}
