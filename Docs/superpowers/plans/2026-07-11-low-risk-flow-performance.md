# 低风险流程性能优化 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在不改PLC读取、动作、锁和调度业务规则的前提下，减少后端循环日志输出和全流程页Tooltip重复刷新。

**Architecture:** 后天车调度仍每轮完整计算并照原条件执行，只将重复的诊断文本改为“变化时输出或每10秒心跳输出”。Tooltip继续从既有内存快照生成，但改为单一状态同步入口调用，避免两条UI同步循环重复格式化相同内容。

**Tech Stack:** C#、WPF MVVM、现有流程引擎。

---

### Task 1: 后天车调度日志限流

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`

- [ ] **Step 1: 为每个后端引擎保存最后一次已输出的调度摘要和时间。**

  摘要必须包含：动作、原因、上料候选数、下料候选数、中转架物理板数、可匹配板数、有效斜床数和高压阈值；心跳间隔固定为10秒。

- [ ] **Step 2: 用摘要门控替换主循环中的无条件调度日志。**

  调度器 `RearSkewDispatchPlanner.Decide(...)`、`TryDispatchLoadAsync(...)`、`TryDispatchUnloadAsync(...)` 和循环延迟保持原样。仅当摘要变化，或距离上次同摘要输出已满10秒，才输出原有完整一行。

- [ ] **Step 3: 保留异常、安全告警和动作开始/结束日志。**

  不调整 `catch`、超时、设备重连、安全暂停和人工确认日志。

### Task 2: Tooltip刷新去重

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [ ] **Step 1: 保留1号线状态同步循环的 `RefreshFlowStatusHoverDetails()` 调用。**

  该方法本来就读取1号线、2号线和研磨的内存快照，因此一个入口可刷新所有Tooltip。

- [ ] **Step 2: 删除2号线状态同步循环中的重复调用。**

  不更改 `RefreshFlowStatusHoverDetails()` 方法内容、其快照来源、卡片属性或轮询间隔。

### Task 3: 验证

**Files:**
- Verify: `AutomaticOnlineHostComputer.csproj`

- [ ] **Step 1: 检查差异，确认改动仅涉及三处目标文件和本计划文件。**

  Run: `git diff -- Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Presentation/ViewModels/Home/HomeViewModel.cs`

- [ ] **Step 2: 编译主项目。**

  Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

  Expected: `0 Error(s)`；现有FANUC互操作警告可存在。

- [ ] **Step 3: 人工代码复核。**

  确认两个后端仍然每轮调用调度器且动作分支完全未改；确认Tooltip仅保留一个刷新入口。
