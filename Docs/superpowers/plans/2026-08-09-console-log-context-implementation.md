# 控制台日志动作上下文与中文化 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在不改变任何业务控制路径的前提下，让核心设备动作日志带唯一动作号、设备/工件/工位上下文和中文字段解释。

**Architecture:** 新增独立的 `OperationalLog` 旁路设施，使用 `AsyncLocal` 保存单趟动作的只读显示上下文。流程入口复用既有 `OperationalEventContextFactory.NewActionId` 创建的动作号，并用 `using` 绑定上下文；设备服务和微调帮助类只读取上下文并格式化日志。`EngineLogRouter` 保持现有的文件归属算法，新的显示标签不参与路由。

**Tech Stack:** .NET 8、C#、xUnit、现有 `Console` + `EngineLogRouter`。

---

## 文件结构

- Create: `Infrastructure/Logging/OperationalLog.cs` — 不抛异常的动作上下文、中文字段格式化和三种级别输出。
- Modify: `Communication/DeviceServices/CraneService.cs` — 对绝对移动的命令/确认/失败节点输出中文操作日志。
- Modify: `Service/FlowEngine/XAbsFineTuneHelper.cs` — 对微调开始、稳定结论、微调命令、反馈失败输出中文日志；不改变采样和微调算法。
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`、`Line2FrontFlowEngine.cs`、`Line1RearFlowEngine.cs`、`Line2RearFlowEngine.cs`、`BalancingFlowEngine.cs`、`GrindingFlowEngine.cs` — 在已生成动作号且已拿到工件的入口绑定/释放显示上下文，补动作开始和暂停后果日志。
- Test: `AutomaticOnlineHostComputer.Tests/Infrastructure/OperationalLogTests.cs` — 格式、嵌套恢复、并发隔离和日志输出失败隔离。
- Test: `AutomaticOnlineHostComputer.Tests/Infrastructure/EngineLogRouterTests.cs` — 新格式在既有作用域下仍写入正确文件，旧前缀路由仍有效。
- Test: `AutomaticOnlineHostComputer.Tests/OperationalEvents/FineTuneInstrumentationContractTests.cs` — 断言微调核心中文事件存在，且既有算法/失败契约未改。

### Task 1: 建立无业务副作用的日志上下文

**Files:**
- Create: `Infrastructure/Logging/OperationalLog.cs`
- Test: `AutomaticOnlineHostComputer.Tests/Infrastructure/OperationalLogTests.cs`

- [ ] **Step 1: 编写失败测试，先定义格式和上下文恢复契约。**

```csharp
[Fact]
public void Info_uses_current_action_context_and_chinese_fields()
{
    using var scope = OperationalLog.BeginAction("2号线前天车", "3号天车", "L2-FRONT-ABC", "E26080158/1", "ST714", "ST502");
    string line = OperationalLog.Format(LogLevel.Info, "移动命令已发送", "已向控制器发送绝对移动命令",
        ("目标X", "119 mm"), ("到位信号INP", "1（有效）"));

    Assert.Equal("[信息][2号线前天车][3号天车][动作=L2-FRONT-ABC][工件=E26080158/1][来源=ST714][目标=ST502][移动命令已发送] 已向控制器发送绝对移动命令 | 目标X=119 mm；到位信号INP=1（有效）", line);
}

[Fact]
public async Task Context_isolated_between_parallel_actions_and_restored_after_dispose()
{
    using var outer = OperationalLog.BeginAction("动平衡", "机械手3", "BAL-M3-OUTER");
    string inner = await Task.Run(() =>
    {
        using var nested = OperationalLog.BeginAction("2号线前天车", "3号天车", "L2-FRONT-INNER");
        return OperationalLog.Format(LogLevel.Info, "到位确认", "已确认到位");
    });

    string restored = OperationalLog.Format(LogLevel.Info, "动作继续", "外层上下文仍有效");
    Assert.Contains("动作=L2-FRONT-INNER", inner);
    Assert.Contains("动作=BAL-M3-OUTER", restored);
}
```

- [ ] **Step 2: 运行定向测试，确认因类型不存在而失败。**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~OperationalLogTests --no-restore`  
Expected: 编译失败，提示 `OperationalLog`、`LogLevel` 不存在。

- [ ] **Step 3: 实现最小的旁路日志设施。**

```csharp
public static class OperationalLog
{
    private static readonly AsyncLocal<LogActionContext?> Current = new();

    public static IDisposable BeginAction(string scope, string device, string actionId,
        string? workpiece = null, string? source = null, string? target = null)
    {
        var previous = Current.Value;
        Current.Value = new LogActionContext(scope, device, actionId, workpiece, source, target);
        return new Lease(previous);
    }

    public static string Format(LogLevel level, string eventName, string conclusion,
        params (string Name, object? Value)[] fields)
    {
        var context = Current.Value;
        var headers = new List<string> { $"[{GetChineseLevel(level)}]" };
        if (context is not null)
        {
            headers.Add($"[{context.Scope}]");
            headers.Add($"[{context.Device}]");
            headers.Add($"[动作={context.ActionId}]");
            AppendHeader(headers, "工件", context.Workpiece);
            AppendHeader(headers, "来源", context.Source);
            AppendHeader(headers, "目标", context.Target);
        }
        headers.Add($"[{eventName}]");
        var values = fields.Where(pair => pair.Value is not null && !string.IsNullOrWhiteSpace(pair.Value.ToString()))
            .Select(pair => $"{pair.Name}={pair.Value}");
        string suffix = string.Join("；", values);
        return string.IsNullOrWhiteSpace(suffix)
            ? $"{string.Concat(headers)} {conclusion}"
            : $"{string.Concat(headers)} {conclusion} | {suffix}";
    }

    public static void Info(string eventName, string conclusion, params (string Name, object? Value)[] fields)
        => TryWrite(LogLevel.Info, eventName, conclusion, fields);
}
```

`TryWrite` 必须 `try/catch` 包住格式化和 `Console.WriteLine`；失败时直接返回。`Lease.Dispose` 必须只恢复之前的 `AsyncLocal` 值。该文件不得读取 PLC、配置、缓存、锁或任何流程状态。

- [ ] **Step 4: 运行定向测试，确认格式和并发隔离通过。**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~OperationalLogTests --no-restore`  
Expected: PASS。

### Task 2: 锁定文件归属与新格式的兼容性

**Files:**
- Modify: `AutomaticOnlineHostComputer.Tests/Infrastructure/EngineLogRouterTests.cs`

- [ ] **Step 1: 为作用域优先于新正文格式写失败测试。**

```csharp
using (EngineLogRouter.BeginScope(EngineLogRouter.Line2))
    router.WriteLine("[信息][2号线前天车][3号天车][动作=L2-FRONT-ABC][到位确认] 已确认到位");

Assert.Contains("动作=L2-FRONT-ABC", File.ReadAllText(Path.Combine(dayDirectory, "line2.log")));
Assert.DoesNotContain("动作=L2-FRONT-ABC", File.ReadAllText(Path.Combine(dayDirectory, "shared.log")));
```

- [ ] **Step 2: 运行路由测试。**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~EngineLogRouterTests --no-restore`  
Expected: PASS；若失败，只修日志测试前提或路由旁路，不得通过改业务作用域修复。

- [ ] **Step 3: 保持 `EngineLogRouter` 不变，记录验证结果。**

新格式由 `OperationalLog` 生成后仍交给既有 `Console`，而现有 `BeginScope(line2)` 已优先决定目的文件；旧 `[Line2Front]` 前缀回退的断言保留。不得让显示正文驱动 `ResolveDestination`。

### Task 3: 在设备和微调层输出中文关键节点

**Files:**
- Modify: `Communication/DeviceServices/CraneService.cs:807-947,989-1077`
- Modify: `Service/FlowEngine/XAbsFineTuneHelper.cs:400-525,597-688`
- Test: `AutomaticOnlineHostComputer.Tests/OperationalEvents/FineTuneInstrumentationContractTests.cs`

- [ ] **Step 1: 在既有微调契约测试中先断言新的事件/中文字段。**

```csharp
Assert.Contains("[绝对值微调开始]", output);
Assert.Contains("稳定采样次数=", output);
Assert.Contains("绝对编码器X=", output);
Assert.Contains("[绝对值微调反馈未跟随]", output);
```

- [ ] **Step 2: 运行测试，确认新输出尚不存在。**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~FineTuneInstrumentationContractTests --no-restore`  
Expected: FAIL，仅因为新日志文本缺失；既有微调结果断言仍应通过。

- [ ] **Step 3: 用 `OperationalLog` 替换关键节点的输出，不改参数或控制分支。**

`CraneService.MoveAbsoluteAsync` 使用如下节点：

```csharp
OperationalLog.Info("绝对移动命令已发送", "已向天车控制器发送绝对移动请求",
    ("目标X", xTarget == -1 ? "保持当前值" : $"{xTarget} mm"),
    ("目标Y", yTarget == -1 ? "保持当前值" : $"{yTarget} mm"),
    ("目标Z", zTarget == -1 ? "保持当前值" : $"{zTarget} mm"),
    ("参与轴", string.Join("、", activeAxes)));
```

到位后输出 `绝对移动到位确认`，字段为 `实际X/实际Y/实际Z/到位信号INP/报警状态`。异常只增加 `绝对移动失败` 日志后立即 `throw;`，不得捕获后吞掉或变更异常类型。

`XAbsFineTuneHelper` 在稳定读数开始、稳定成功、发起微调、反馈重读、最终反馈未跟随处输出 `绝对值微调开始`、`绝对值读数稳定`、`绝对值微调命令已发送`、`绝对值反馈等待刷新`、`绝对值微调反馈未跟随`。`StableReadMaxAttempts`、延时、窗口计算、`MoveAbsoluteAsync` 参数、抛出条件和异常文本均保持不变。

- [ ] **Step 4: 运行微调定向测试。**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~FineTuneInstrumentationContractTests --no-restore`  
Expected: PASS；原有成功、超差、未跟随和失败上报场景均不变。

### Task 4: 在核心动作入口绑定动作号和业务上下文

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs:1802,2296`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs:1812,2302`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:1434,2283`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:1387,2231`
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs:1059,1600,2260`
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs:1414,2050`

- [ ] **Step 1: 在每个已有 `actionId` 且已经获得 `WorkpieceCache` 的入口创建日志作用域。**

前天车/后天车/研磨入口统一紧随 `actionId` 和 `wp` 可用之后加入：

```csharp
using var logAction = OperationalLog.BeginAction(
    "2号线前天车", $"{CraneFront2No}号天车", actionId,
    wp.IdentityText, "ST714", "中转架");
OperationalLog.Info("搬运动作开始", "已创建本趟自动搬运任务",
    ("板号与序号", wp.IdentityText), ("来源工位", "ST714"), ("目标工位", "中转架"));
```

各流程必须采用真实既有站号/设备号：前天车 Pos3→打号机→中转架、后天车中转架→斜床或斜床→下料架、M2 动平衡路线、M3 研磨上料路线、研磨天车 ST709→实际研磨站或实际研磨站→下料架。不得凭名称猜测工位。

- [ ] **Step 2: 将现有 M3 `ConfirmM720EmptyBeforePlaceAsync` 的三类等待日志补成中文高价值日志。**

```csharp
OperationalLog.Warn("等待PLC放料许可", "PLC暂时不允许放料，机械手保持安全等待",
    ("信号名称", "M720放料允许"), ("当前值", "0（暂不允许）"),
    ("工件状态", "保持持件"), ("位置锁", "M821、M720保持"));

OperationalLog.Info("PLC放料许可恢复", "PLC已允许放料，继续原流程",
    ("信号名称", "M720放料允许"), ("当前值", "1（允许）"));
```

保留原来的无限等待、读 PLC 次数、日志节流周期、锁和异常传播，只有文字输出变化。

- [ ] **Step 3: 在各流程原有异常/暂停分支追加原因与后果两条日志。**

```csharp
OperationalLog.Error("搬运动作失败", "流程安全校验失败，当前动作已转人工确认",
    ("异常类型", ex.GetType().Name), ("失败原因", ex.Message));
OperationalLog.Error("引擎暂停", "因物理状态未完成确认，已暂停相关引擎",
    ("自动回队", "禁止"), ("自动重试", "禁止"));
```

只能放在原有 catch 已经决定暂停的位置，不能新增暂停、不能移动 `Pause()` 调用，也不能改变 `finally` 的锁释放顺序。

### Task 5: 全量验证与变更审查

**Files:**
- Modify: `Docs/日志审查与故障定位手册.md`（追加新日志字段表、事件中文名和文件路由说明）

- [ ] **Step 1: 更新操作员手册。**

写明固定方括号字段、动作号如何关联同趟日志、英文/PLC 字段的中文解释、五个日志文件的归属，以及“显示字段不决定文件归属”的约束。

- [ ] **Step 2: 运行定向日志和微调测试。**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter "FullyQualifiedName~OperationalLogTests|FullyQualifiedName~EngineLogRouterTests|FullyQualifiedName~FineTuneInstrumentationContractTests" --no-restore`  
Expected: PASS。

- [ ] **Step 3: 运行完整构建与全量测试。**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`  
Expected: `0 Warning(s)`、`0 Error(s)`。

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-build --no-restore`  
Expected: 全部测试通过。

- [ ] **Step 4: 审查业务逻辑未改变。**

Run: `git diff --check` 和 `git diff --stat`。逐一确认生产代码的修改仅为 `using`、`using var logAction` 和 `OperationalLog` 输出调用；不得存在 PLC 方法参数、`if` 条件、`await Task.Delay`、锁操作、异常类型或 `Pause` 调用的变化。
