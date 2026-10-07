using AutomaticOnlineHostComputer.Infrastructure.Logging;

namespace AutomaticOnlineHostComputer.Tests.Infrastructure;

public sealed class OperationalLogTests
{
    [Fact]
    public void Format_uses_current_action_context_and_chinese_fields()
    {
        using var scope = OperationalLog.BeginAction(
            "2号线前天车", "3号天车", "L2-FRONT-ABC", "E26080158/1", "ST714", "ST502");

        string line = OperationalLog.Format(
            OperationalLogLevel.Information,
            "移动命令已发送",
            "已向控制器发送绝对移动命令",
            ("目标X", "119 mm"),
            ("到位信号INP", "1（有效）"));

        Assert.Equal(
            "[信息][2号线前天车][3号天车][动作=L2-FRONT-ABC][工件=E26080158/1][来源=ST714][目标=ST502][移动命令已发送] 已向控制器发送绝对移动命令 | 目标X=119 mm；到位信号INP=1（有效）",
            line);
    }

    [Fact]
    public async Task Context_is_isolated_between_parallel_actions_and_restored_after_dispose()
    {
        using var outer = OperationalLog.BeginAction("动平衡", "机械手3", "BAL-M3-OUTER");

        string[] parallel = await Task.WhenAll(
            Task.Run(() =>
            {
                using var action = OperationalLog.BeginAction("2号线前天车", "3号天车", "L2-FRONT-INNER");
                return OperationalLog.Format(OperationalLogLevel.Information, "到位确认", "已确认到位");
            }),
            Task.Run(() =>
            {
                using var action = OperationalLog.BeginAction("研磨", "5号天车", "GRIND-LOAD-INNER");
                return OperationalLog.Format(OperationalLogLevel.Information, "上料开始", "已开始上料");
            }));

        string restored = OperationalLog.Format(OperationalLogLevel.Information, "动作继续", "外层上下文仍有效");

        Assert.Contains("动作=L2-FRONT-INNER", parallel[0]);
        Assert.DoesNotContain("GRIND-LOAD-INNER", parallel[0]);
        Assert.Contains("动作=GRIND-LOAD-INNER", parallel[1]);
        Assert.DoesNotContain("L2-FRONT-INNER", parallel[1]);
        Assert.Contains("动作=BAL-M3-OUTER", restored);
    }

    [Fact]
    public void Format_omits_unknown_context_and_empty_fields_without_throwing()
    {
        string line = OperationalLog.Format(
            OperationalLogLevel.Warning,
            "等待PLC许可",
            "PLC暂时不允许继续",
            ("信号名称", "M720放料允许"),
            ("空字段", null));

        Assert.Equal("[警告][等待PLC许可] PLC暂时不允许继续 | 信号名称=M720放料允许", line);
    }
}
