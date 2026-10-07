using AutomaticOnlineHostComputer.Infrastructure.Logging;

namespace AutomaticOnlineHostComputer.Tests.Infrastructure;

public sealed class EngineLogRouterTests
{
    [Fact]
    public void Scope_has_priority_and_known_prefixes_route_to_the_expected_daily_files()
    {
        string root = Path.Combine(Path.GetTempPath(), $"aohc-log-router-{Guid.NewGuid():N}");
        try
        {
            using (var router = new EngineLogRouter(new StringWriter(), root))
            {
                using (EngineLogRouter.BeginScope(EngineLogRouter.Line1))
                    router.WriteLine("[CraneService] 来自1号线任务的共享设备日志");

                using (EngineLogRouter.BeginScope(EngineLogRouter.Line2))
                using (OperationalLog.BeginAction("2号线前天车", "3号天车", "L2-FRONT-ROUTE"))
                    router.WriteLine("[Line2Front] 新动作上下文仍按原始前缀归档");

                router.WriteLine("  [Line2Front] 由前缀兜底的2号线日志");
                router.WriteLine("[平衡引擎#1] 由前缀兜底的动平衡日志");
                router.WriteLine("[M2] 不作为首前缀映射，落共享日志");
            }

            string dayDirectory = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd"));
            string line1 = File.ReadAllText(Path.Combine(dayDirectory, "line1.log"));
            string line2 = File.ReadAllText(Path.Combine(dayDirectory, "line2.log"));
            string balance = File.ReadAllText(Path.Combine(dayDirectory, "balance.log"));
            string shared = File.ReadAllText(Path.Combine(dayDirectory, "shared.log"));

            Assert.Contains("[CraneService] 来自1号线任务的共享设备日志", line1);
            Assert.Contains("[Line2Front] 由前缀兜底的2号线日志", line2);
            Assert.Contains("动作=L2-FRONT-ROUTE", line2);
            Assert.DoesNotContain("动作=L2-FRONT-ROUTE", shared);
            Assert.Contains("[平衡引擎#1] 由前缀兜底的动平衡日志", balance);
            Assert.Contains("[M2] 不作为首前缀映射，落共享日志", shared);
            Assert.StartsWith("[", line1);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
