# 动平衡机械手提前释放位置锁 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在不丢失工件身份或误删新来源缓存的前提下，让机械手2/3按实际 Y 位置提前释放对应位置锁。

**Architecture:** 来源缓存只在 X11=1 且 Z 安全确认后原子转为机械手在途状态。`CraneService` 在其原有绝对移动轮询中向调用方报告只读状态；M2 在去 ST008 的 Y 途中跨阈值释放来源锁，M3 在 ST010 闭环后的回程途中跨阈值释放 M720/M821。异常、应急和人工结案均以动作在途状态而非同名来源缓存作为旧工件事实。

**Tech Stack:** C# / .NET 8 / WPF / Modbus TCP / xUnit。

---

### Task 1: 配置模型和配置页面

**Files:**
- Modify: `Infrastructure/Config/MotionConfig.cs`
- Modify: `Config/motion_settings.json`
- Modify: `Presentation/ViewModels/Config/ConfigPageViewModel.cs`
- Modify: 配置页面对应 XAML
- Test: `AutomaticOnlineHostComputer.Tests/Infrastructure/`

- [ ] 新增 `BalancingSection.M2ReleaseLocksBelowY` 与 `BalancingSection.M3ReleaseLocksBelowY` 整数属性，并按 JSON 默认序列化。
- [ ] 在 `balancing` JSON 节新增两个字段，在配置页面暴露、保存和即时通知属性变化。
- [ ] 不增加任何范围、上下限、来源坐标或安全位关联校验。
- [ ] 编写契约测试，断言两个字段存在、可保存和不因负数或任意整数被拒绝。

### Task 2: 绝对移动状态观察

**Files:**
- Modify: `Communication/DeviceServices/CraneService.cs`
- Test: `AutomaticOnlineHostComputer.Tests/Communication/`

- [ ] 为 `MoveAbsoluteAsync` 添加可选、只读的状态观察参数或等价新入口，并由既有 `PollAxesAsync` 每次成功状态读取调用。
- [ ] 保持目标写入、触发位、轮询频率、超时、压力停止和 finally 复位语义不变。
- [ ] 回调只接收状态，不在服务层执行业务释放；读取失败、取消、超时不伪造跨越事件。
- [ ] 编写测试覆盖：Y 从阈值上方到下方报告一次跨越；未跨越不报告；移动异常不把未确认位置当作跨越。

### Task 3: 来源缓存转在途状态

**Files:**
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs`
- Test: `AutomaticOnlineHostComputer.Tests/FlowEngine/`

- [ ] 新增受同步保护的 M2/M3 在途工件记录，保存完整工件、来源、目标、动作版本和归属阶段。
- [ ] 在 M2/M3 的 X11=1 且 Z 安全确认后，以无 await 的原子辅助方法把适用来源缓存转入在途状态。
- [ ] 正常目标闭环后清理对应在途记录；不再在目标退磁后按来源键删除旧缓存。
- [ ] 编写测试：旧动作在途时后天车写入同名新缓存，旧动作正常完成不会删除新缓存。

### Task 4: M2/M3 提前释放和恢复保护

**Files:**
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs`
- Modify: `Service/FlowEngine/FlowActionManualRegistry.cs`（仅在现有快照不能表达在途事实时）
- Test: `AutomaticOnlineHostComputer.Tests/FlowEngine/`

- [ ] M2 在 M824（如适用）和 M710 二次确认后，使用去 ST008 的运动观察，在真实 Y 跨越 `M2ReleaseLocksBelowY` 时释放本趟实际持有的 M818、M817 锁。
- [ ] M3 在 ST010 退磁、Z 安全、M721、FIFO 和来源账本闭环后，使用回安全位的运动观察，在真实 Y 跨越 `M3ReleaseLocksBelowY` 时依序释放 M720、M821。
- [ ] 保持 Busy、动作版本、取消令牌、任务牌和在途工件至整趟动作结束。
- [ ] 修改应急清理和人工结案：提前释放后只处理旧动作的在途工件，禁止按来源键删除或覆盖后天车新缓存。
- [ ] 编写测试覆盖 M817、M818、M821、M700 四种来源、阈值前异常、阈值后异常、重复释放和新缓存保护。

### Task 5: 核心业务文档与验证

**Files:**
- Modify: `Docs/全线业务逻辑详解.md`
- Modify: `Docs/superpowers/specs/2026-08-23-balancing-manipulator-early-lock-release-design.md`（仅在实施发现设计与代码必须调整时）

- [ ] 详细更新“11.3 机械手2完整动作”和“11.5 机械手3完整动作”，以编号步骤说明缓存转在途、锁保持、实际 Y 跨阈值、提前释放、目标闭环和异常/人工恢复。
- [ ] 同步异常规则，明确不能按来源键删除新缓存。
- [ ] 运行相关 xUnit 测试和主项目构建；检查 Git 差异确保仅包含本任务文件及用户已有改动。
- [ ] 不提交 Git，向用户报告构建/测试原始结果。
