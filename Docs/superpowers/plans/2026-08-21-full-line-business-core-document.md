# 全线业务逻辑核心文档 Implementation Plan

> **For agentic workers:** 本计划在当前任务内直接执行；不分派子代理，不创建 Git 提交。

**Goal:** 将《全线业务逻辑详解》重构为以当前源码为准、覆盖五台天车全部步骤和状态机、全篇使用编号步骤且不含表格的核心业务文档。

**Architecture:** 单一主文档按“全局规则 → 工件流向 → 五台天车动作 → 异常恢复与诊断”组织。分散文档只作为核对来源，有效内容合并到主文档，主文档不依赖读者跳转到其他文档才能理解核心流程。

**Tech Stack:** Markdown、C# 流程引擎源码、设备服务与地址定义、PowerShell/rg 静态核对。

---

### Task 1: 建立源码与文档覆盖清单

**Files:**

- Read: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Read: `Service/FlowEngine/Line2FrontFlowEngine.cs`
- Read: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Read: `Service/FlowEngine/Line2RearFlowEngine.cs`
- Read: `Service/FlowEngine/GrindingFlowEngine.cs`
- Read: `Service/FlowEngine/BalancingFlowEngine.cs`
- Read: `Service/FlowEngine/TransferRackWorkpieceLedger.cs`
- Read: `Service/FlowEngine/FlowActionContext.cs`
- Read: `Service/FlowEngine/FlowActionManualRegistry.cs`
- Read: `Service/FlowEngine/EmergencyActionLease.cs`

- [ ] 核对每个引擎的启动、暂停、主循环、调度入口、动作方法、catch、finally 和应急入口。
- [ ] 记录五台天车的锁外候选、锁内复核、工件所有权边界和状态写入顺序。
- [ ] 核对分散文档中已实现但主文档遗漏的规则。

### Task 2: 重建主文档骨架与全局规则

**Files:**

- Modify: `Docs/全线业务逻辑详解.md`

- [ ] 写明文档用途、当前代码口径和五台天车职责。
- [ ] 写明任务、工件、来源预约、在途、目标待交接和设备内存状态的区别。
- [ ] 写明共享锁、碰撞区、XYZ、X11、X2、速度、动作版本和超时的统一规则。
- [ ] 删除旧文档中的重复章节编号和表格式速查内容。

### Task 3: 重写两条线前端与两台前天车流程

**Files:**

- Modify: `Docs/全线业务逻辑详解.md`

- [ ] 按编号步骤写任务入口、线路分配、机械手1取料和货叉交接。
- [ ] 按编号步骤写双头镗加工与快速绕过路径。
- [ ] 分别写1号线、2号线前天车从货叉取板、打号、放中转架和归位全过程。
- [ ] 写清 `_currentWp`、`_forkPhase`、前天车队列、中转架缓存和动作账本状态机。

### Task 4: 重写两台后天车与斜床流程

**Files:**

- Modify: `Docs/全线业务逻辑详解.md`

- [ ] 写清上料/下料压力调度、通道不可用时的下料让位和候选扫描。
- [ ] 写清中转架普通预约、人工冻结、原子预约失败和来源结案。
- [ ] 分别写1号线、2号线后天车取中转架、上斜床、斜床加工、下料和目的地选择。
- [ ] 写清斜床 `Idle/Loading/Machining/WaitingUnload/Unloading` 的进入和退出条件。
- [ ] 写清动作异常后斜床状态、工件缓存、锁、任务牌和正确恢复入口。

### Task 5: 重写动平衡与研磨天车流程

**Files:**

- Modify: `Docs/全线业务逻辑详解.md`

- [ ] 写清机械手2和机械手3的派发条件、M817/M818/M819/M820交接与 Busy 状态。
- [ ] 写清研磨机扫描、请求数据、磨石报警、联机模式、PendingWorkpiece 和上料优先策略。
- [ ] 写清研磨天车 ST709 上料、D200补长、显示屏旁路写入、研磨机参数下发、取放料和归位。
- [ ] 写清研磨下料、ST710放料、FIFO和各类超时/通信失败处理。

### Task 6: 合并异常恢复、状态清理与诊断规则

**Files:**

- Modify: `Docs/全线业务逻辑详解.md`
- Read: `Docs/异常处理与人工恢复操作手册.md`
- Read: `Docs/搬运动作状态机_异常处理与人工恢复审查说明.md`

- [ ] 按工件仍在来源、载具持件、目标待交接和人工移走四类事实写结案步骤。
- [ ] 写清来源结案、目标设备应急、清空机器状态、恢复内存状态和 Resume 的区别。
- [ ] 写清五台天车发生运动超时后的暂停、停止、锁释放和恢复步骤。
- [ ] 写清有数据却不上料、状态显示上料中但无工件、请求信号正常但不动作的诊断顺序。

### Task 7: 静态验证核心文档

**Files:**

- Verify: `Docs/全线业务逻辑详解.md`

- [ ] 检查文档不存在 Markdown 表格行。
- [ ] 检查一级、二级章节编号没有重复。
- [ ] 检查五台天车名称、五类斜床状态、三类研磨状态和三类来源预约状态均有定义。
- [ ] 检查不存在 `TODO`、`TBD`、空标题和明显旧日期补丁标题。
- [ ] 运行 `git diff --check -- Docs/全线业务逻辑详解.md`，确认无空白错误。
- [ ] 更新 `task_plan.md`、`findings.md` 和 `progress.md`，记录修改范围与验证结果。

