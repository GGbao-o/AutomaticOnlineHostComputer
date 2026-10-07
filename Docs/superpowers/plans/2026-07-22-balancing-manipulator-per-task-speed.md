# 动平衡机械手按任务设置速度 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让机械手2/3在每趟自动搬运首次物理移动前按当前配置写一次速度，并把动平衡流程文档与源码对齐。

**Architecture:** 在 `BalancingFlowEngine` 内收敛为一个读取 `MotionConfig` 并写九个绝对速度寄存器的私有辅助方法。M2/M3 在所有非物理前置检查通过后调用它；初始化只负责连接。文档以完整业务逻辑详解为权威，并同步简明流程总览。

**Tech Stack:** C#/.NET 8、WPF、Modbus TCP、xUnit。

---

### Task 1: 任务级速度回归测试

**Files:**
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/BalancingManipulatorSpeedContractTests.cs`

- [ ] 编写静态契约测试，确认 M2/M3 都通过同一速度辅助方法、各自在首次 `MoveAbsoluteAsync` 前调用一次，初始化块不再直接调用 `SetAbsSpeedAsync`。
- [ ] 先运行筛选测试，预期旧代码失败，因为初始化仍写速度且流程内没有任务级速度设置。

### Task 2: 动平衡引擎速度策略

**Files:**
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs:684-714, 1121-1123, 1606-1608`

- [ ] 新增 `ConfigureManipulatorAbsSpeedAsync(CraneService manipulator, int manipulatorNo, string taskName, CancellationToken ct)`：调用 `_cfg.GetManipulatorSpeed(manipulatorNo)`，完整调用一次 `SetAbsSpeedAsync`，并记录任务级日志。
- [ ] 初始化块保留连接，移除 M2/M3 的直接速度写入。
- [ ] 在 M2/M3 各自 X11 残留检查通过后、第一条 `MoveAbsoluteAsync` 前调用辅助方法；不在任务其余移动中调用。
- [ ] 重新运行契约测试，预期通过。

### Task 3: 业务逻辑和流程总览文档

**Files:**
- Modify: `Docs/全线业务逻辑详解.md:714-812,1029-1076`
- Modify: `Docs/03-设备交互流程/整线引擎流程总览_按代码.md:127-145`

- [ ] 在 M2/M3 完整流程中补齐任务级速度、九寄存器、来源与目标、来源优先级、锁、二次确认、X11、完成位、缓存与暂停边界。
- [ ] 将速度章节从“动平衡初始化一次”改为“每趟 M2/M3 任务首次物理移动前一次”，说明同一趟不重复设置、配置下一趟生效。
- [ ] 扩充流程总览 M2/M3 的简表，使其与完整业务逻辑文档一致。

### Task 4: 验证

**Files:**
- Test: `AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj`

- [ ] 运行新增测试。
- [ ] 运行完整测试套件。
- [ ] 运行主项目构建和 `git diff --check`。
