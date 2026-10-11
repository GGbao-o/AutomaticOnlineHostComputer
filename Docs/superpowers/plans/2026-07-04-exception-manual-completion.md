# 全量异常、暂停、弹窗、锁释放与恢复手册 Implementation Plan

> **2026-10-11现行差异提示：** 人工结案器仍存在，但六条斜床/研磨动作可能先自动删除匹配快照、Wp/Pending，旧目标身份或任务牌不再匹配时结案会被拒绝。保板续跑不作为通用保证。 当前规则见[业务总文档](../../全线业务逻辑详解.md)与[现场处置](../../运维/异常弹窗与现场故障处置.md)。下文为历史记录，不覆盖上述现行说明。


> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 全面重写 `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`，将源码及Docs中分散的异常、暂停、弹窗、锁释放和恢复规则归并为一份按代码可追溯的权威手册。

**Architecture:** 以当前源码为最高证据，先建立异常边界清单，再按业务模块编写固定场景模板，最后增加finally专项、应急恢复矩阵、已知风险和代码索引。其他文档只作为场景发现来源；与源码冲突时正文采用源码行为，并记录差异。

**Tech Stack:** C#/.NET 8、WPF、Markdown、PowerShell、ripgrep

---

### Task 1: 建立源码与文档覆盖清单

**Files:**
- Modify: `findings.md`
- Modify: `progress.md`

- [ ] **Step 1: 枚举生产异常边界**

Run:

```powershell
rg -n "catch \(|finally|throw new |On.*Alarm\?\.Invoke|MessageBox\.Show" Service Communication Presentation Views -g "*.cs"
```

Expected: 输出四个线体引擎、动平衡、研磨、通信服务、HomeViewModel和应急对话框中的异常边界。

- [ ] **Step 2: 枚举Docs中的异常与恢复章节**

Run:

```powershell
rg -n "^#{1,5} .*异常|^#{1,5} .*恢复|catch|finally|暂停|弹窗|应急" Docs -g "*.md"
```

Expected: 输出业务逻辑、通信分析、日志手册、设备交互流程和协议文档中的异常资料。

- [ ] **Step 3: 将覆盖清单记录到审计文件**

在 `findings.md` 中记录模块、核心方法、异常边界、关联文档和待合并章节；在 `progress.md` 记录完成时间和检索统计。

### Task 2: 重写手册基础章节

**Files:**
- Modify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`

- [ ] **Step 1: 写审查基线和异常传播原理**

内容必须解释 `throw`、内外层 `catch`、`finally`、告警事件、HomeViewModel暂停和MessageBox之间的真实调用关系，并明确“catch不等于弹窗、暂停不等于设备停止”。

- [ ] **Step 2: 写变量、状态、缓存和锁术语表**

逐项解释规格中列出的变量，注明“命令标志、设备反馈或软件推断”属性。

- [ ] **Step 3: 写主页面弹窗入口与去重机制**

以 `Presentation/ViewModels/Home/HomeViewModel.cs` 和应急对话框为证据，列出标题、暂停范围、去重标志、复位时机和第二条告警被抑制的行为。

### Task 3: 补全总调度与1/2号线前端异常

**Files:**
- Modify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`
- Source: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Source: `Service/FlowEngine/Line2FrontFlowEngine.cs`
- Source: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [ ] **Step 1: 写总上料调度和状态刷新异常**

覆盖读取失败、快照失效、后台重连、主循环异常和不弹窗场景。

- [ ] **Step 2: 写机械手1所有物理阶段异常**

分别覆盖取料前X11、下压保护、三次X11为0、X11已持件、已放货叉未开门闩、M801失败、1号线第二段回位失败和finally释放共享机械手锁。

- [ ] **Step 3: 写货叉与双头镗状态机异常**

按 `_forkPhase` 解释参数、R6102、R6103、M912、R6104、R6107、M913/M914、R6108、后台任务和命令结果未知。

- [ ] **Step 4: 写前天车、打号和中转架异常**

区分未充磁、X11已确认、工件在打号机、重新取料、已放中转架、缓存回调失败、共享区锁释放和前天车finally风险。

### Task 4: 补全1/2号线后端异常

**Files:**
- Modify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`
- Source: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Source: `Service/FlowEngine/Line2RearFlowEngine.cs`
- Source: `Service/FlowEngine/EmergencyActionLease.cs`

- [ ] **Step 1: 写轮询、状态同步和派发异常**

覆盖SignalFresh、轮询超时、断线重连、WpRecoveryNeeded、Machining到Idle同步和任务派发锁。

- [ ] **Step 2: 写后天车上料全部阶段**

覆盖缓存移除、共享区锁、中转架取料、X11、斜床参数、等待信号、装料、退磁、CNC完成、取消和finally退避。

- [ ] **Step 3: 写后天车下料全部阶段**

覆盖三次X11、两阶段安全恢复、尾座、分流、目的位锁、物理放下、缓存与PLC闭环、Unloading残留和应急Idle恢复。

- [ ] **Step 4: 写1/2号线差异**

明确1号线Modbus/FANUC混用、M817/M720路径和2号线全FANUC、M818/M821路径，不用“另一条线同理”替代关键差异。

### Task 5: 补全动平衡与研磨异常

**Files:**
- Modify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`
- Source: `Service/FlowEngine/BalancingFlowEngine.cs`
- Source: `Service/FlowEngine/GrindingFlowEngine.cs`

- [ ] **Step 1: 写M2异常**

覆盖来源缓存、直径、位置锁、X11、放ST008、M711、取消和finally。

- [ ] **Step 2: 写M3异常**

覆盖M700/M821两种来源、D100、M825、M720、M721、研磨FIFO、取消和finally。

- [ ] **Step 3: 写研磨上料异常**

覆盖ST709缓存/M730、X11、M731、研磨机握手、磨石报警、已放机内、PendingWorkpiece和天车锁。

- [ ] **Step 4: 写研磨下料异常**

覆盖等待下料、尾座/门、ST710/M720、退磁、M721、状态卡死保护、恢复标记和应急。

### Task 6: 补全通信层与finally专项

**Files:**
- Modify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`
- Source: `Communication/Clients/ModbusTcpClient.cs`
- Source: `Communication/Clients/MitsubishiMcClient.cs`
- Source: `Communication/Clients/SyntecCncClient.cs`
- Source: `Communication/DeviceServices/CraneService.cs`
- Source: `Communication/DeviceServices/FanucSkewBedService.cs`

- [ ] **Step 1: 写通信异常传播链**

解释单笔硬超时、连接关闭、重连次数、FANUC Worker隔离、X区读取失败抛异常和非幂等命令结果未知。

- [ ] **Step 2: 建立生产finally清单**

逐方法列出锁、标志、设备动作、令牌、异常覆盖风险和物理安全结论。

- [ ] **Step 3: 对比catch与finally职责**

说明catch负责业务分类，finally只保证执行而不保证安全；单列当前finally内运动和无条件共享区放锁风险。

### Task 7: 归并应急、恢复和其他文档内容

**Files:**
- Modify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`

- [ ] **Step 1: 写清空状态与应急处理差异**

覆盖斜床清空寄存器不改Idle、斜床应急设置Idle并清Wp、6秒退出门槛、设备清零失败二次确认和锁释放范围。

- [ ] **Step 2: 写恢复决策矩阵**

按自动恢复、点击启动、应急处理、人工补信号和禁止恢复分类全部场景。

- [ ] **Step 3: 合并其他Docs中的独有场景**

逐项核对并归并五份核心异常文档、设备交互流程和通信协议中的异常章节；冲突内容记录到“文档差异与纠正”。

- [ ] **Step 4: 写当前风险和源码索引**

风险必须标注“尚未修改”，索引必须包含文件、方法和当前行号。

### Task 8: 验证完整性与交付

**Files:**
- Verify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`
- Modify: `task_plan.md`
- Modify: `findings.md`
- Modify: `progress.md`

- [ ] **Step 1: 验证结构和占位符**

Run:

```powershell
rg -n "^## " Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md
rg -n "TBD|TODO|待补充|持续补充|FIXME" Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md
```

Expected: 15个主题全部存在；占位符检索无输出。

- [ ] **Step 2: 验证关键变量、模块和恢复入口**

Run:

```powershell
rg -n "_currentWp|_forkPhase|bed\.St|bed\.Wp|PendingWorkpiece|EmergencyActionLease|finally|共享区锁|斜床应急|清空状态" Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md
```

Expected: 每个关键术语均有定义和场景引用。

- [ ] **Step 3: 验证Markdown和改动范围**

Run:

```powershell
git diff --check -- Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md
git status --short -- Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md task_plan.md findings.md progress.md
```

Expected: `git diff --check` 无输出；只有文档和审计记录发生本任务相关改动。

- [ ] **Step 4: 更新审计记录并交付**

在 `task_plan.md` 标记阶段完成；在 `findings.md` 记录纠正的文档差异；在 `progress.md` 记录最终字数、章节数和验证结果。
