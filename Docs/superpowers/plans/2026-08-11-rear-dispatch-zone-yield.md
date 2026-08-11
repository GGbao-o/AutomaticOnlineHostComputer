# 后天车区域锁让位调度 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 上料通道被占而存在立即可下料斜床时，不创建上料任务，改派后天车下料。

**Architecture:** 纯调度器保留基础压力决策，并提供可测试的让位覆盖方法。1、2号线后端引擎根据两把区域锁和下料来源站筛出立即可下料候选，将其站号传入下料派发复核；不改变实际运动和锁顺序。

**Tech Stack:** .NET 8、C#、xUnit。

---

### Task 1: 锁定纯调度器让位规则

**Files:**
- Modify: `Service/FlowEngine/RearSkewDispatchPlanner.cs`
- Create: `AutomaticOnlineHostComputer.Tests/FlowEngine/RearSkewDispatchPlannerTests.cs`

- [x] 为 `Load` 决策增加“通道忙且立即下料数大于零时覆盖为 `Unload`”的纯函数，保留基础决策的下料压力阈值。
- [x] 测试通道忙覆盖、通道空闲不覆盖、无立即下料不覆盖。

### Task 2: 接入 1、2 号线派发前判断

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`

- [x] 候选扫描后读取 `ZoneMT/ZoneTS` 当前空闲状态，筛出普通斜床以及 ZoneTS 空闲时的 ST108/ST606 下料候选。
- [x] 覆盖为下料时将首个立即可下料的站号传给下料派发；锁内复核找不到该站时释放后天车锁并等待下一轮，不改派其它候选。
- [x] 保持现有 `DoLoad/DoUnload` 的锁、预约和物理命令不变。

### Task 3: 更新业务文档并验证

**Files:**
- Modify: `Docs/全线业务逻辑详解.md`

- [x] 在 4.1 和 4.2 说明基础压力决策之后的区域通道让位规则、立即可下料定义和无候选时保留上料等待的边界。
- [x] 运行调度器测试和应用编译；检查差异不包含 PLC 通讯或实际运动服务。
