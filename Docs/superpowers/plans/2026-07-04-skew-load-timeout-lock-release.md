# ST108/ST606上料异常退避与锁释放 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 仅修改1、2号线后天车上料异常处理，使Z已下降时不执行X+1000，同时所有异常收尾释放本次动作持有的锁并用一次弹窗说明退避和锁释放结果。

**Architecture:** 在现有`LoadHandshakeState`中增加“Z可能已下降”和“Z已回安全位”标志；正常业务动作顺序保持不变。`catch`只生成待弹出的业务异常正文，`finally`根据阶段决定是否退避、幂等释放锁、补充收尾结果，然后统一触发安全弹窗。

**Tech Stack:** C# 12、.NET 8、WPF、现有`EmergencyActionLease`与`CraneService`。

---

### Task 1: 1号线上料异常阶段和统一弹窗

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:122-126`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:1125-1360`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:1395-1438`

- [ ] **Step 1: 扩展异常阶段状态**

在`LoadHandshakeState`增加`ZMayBeDown`和`ZSafeAfterPlace`，并写明它们只服务异常收尾：Z下降命令发出前置true，退磁后Z成功回安全位才清false并置安全标志。

- [ ] **Step 2: 在不改变正常顺序的情况下标记阶段**

在原Z下降调用前设置`ZMayBeDown=true`；原退磁后Z回安全调用成功后设置`ZMayBeDown=false`和`ZSafeAfterPlace=true`。不调整任何信号、坐标、等待和动作顺序。

- [ ] **Step 3: 延迟安全弹窗到finally**

增加`pendingSafetyAlarm`局部变量。原持件和已放床catch保持状态处理，只把弹窗正文保存到变量，避免finally结果被第二次弹窗去重吞掉。

- [ ] **Step 4: 按阶段处理ST108异常退避**

Z可能已下降时跳过X退避；Z未下降或已确认回安全位时调用原X+1000退避。无论退避成功、失败或跳过，都使用`operation.TryRelease`释放共享区锁，再释放后天车锁。

- [ ] **Step 5: 统一触发一次安全弹窗**

在锁释放后给`pendingSafetyAlarm`追加“X退避成功/失败/因Z可能下降未执行”和“后天车锁、共享区锁已释放”；非共享斜床追加“后天车锁已释放”。随后调用一次`OnRearCraneSafetyAlarm`。

### Task 2: 2号线应用同一异常策略

**Files:**
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:116-120`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:1089-1320`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:1352-1387`

- [ ] **Step 1: 增加与1号线相同的阶段字段和注释**

- [ ] **Step 2: 在ST606上料Z下降前和Z安全返回后更新阶段**

- [ ] **Step 3: 把catch即时弹窗改为待finally统一弹窗**

- [ ] **Step 4: ST606按阶段决定X+1000，但所有结果均释放共享区锁和后天车锁**

- [ ] **Step 5: 弹窗写明实际退避结果和已释放的锁**

### Task 3: 更新异常恢复文档

**Files:**
- Modify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`
- Modify: `Docs/1_2号线步骤级异常与恢复审计_2026-07-04.md`

- [ ] **Step 1: 把旧的“低Z时仍X+1000”和“退避失败保留共享锁”描述更新为修改后的实际行为**

- [ ] **Step 2: 写明三个超时阶段分别是否执行X退避、释放哪些锁、弹窗内容和斜床应急恢复要求**

- [ ] **Step 3: 保留现场风险声明：锁已释放不等于物理设备已经安全，禁止直接点击启动**

### Task 4: 静态验证和编译

**Files:**
- Verify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Verify: `Service/FlowEngine/Line2RearFlowEngine.cs`
- Verify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`

- [ ] **Step 1: 搜索两个DoLoad，确认上料catch不再提前触发安全弹窗**

Run: `rg -n "pendingSafetyAlarm|ZMayBeDown|ZSafeAfterPlace|OnRearCraneSafetyAlarm" Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs`

- [ ] **Step 2: 检查格式错误**

Run: `git diff --check -- Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md Docs/1_2号线步骤级异常与恢复审计_2026-07-04.md`

- [ ] **Step 3: 编译主项目**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: 构建成功，0个错误；现有警告如仍存在需与本次差异区分。

- [ ] **Step 4: 核对最终差异范围**

只允许上述两个引擎文件和异常文档出现本次新增差异；不得修改正常信号地址、超时时间、坐标公式和正常动作顺序。

### Task 5: ST108下料异常采用同一锁释放策略

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`的`DoUnload`

- [ ] **Step 1: 增加下料异常阶段局部状态**

增加`zMayBeDown`、`sharedRetreatAttempted`、`sharedRetreatSucceeded`和`pendingSafetyAlarm`。Z下降命令以前置`zMayBeDown=true`，X11失败安全回升或正常Y回位/Z回安全成功后置false。

- [ ] **Step 2: 保留正常提前退避，仅记录失败结果**

正常下料到达“尾座张开、Y回位、Z安全、CNC下料完成”后仍执行原X+1000。成功则提前释放共享区锁；失败只记录结果并抛异常，不在finally重复X运动。

- [ ] **Step 3: 将下料安全弹窗延迟到finally**

X11安全恢复失败、X11持件异常、共享区退避失败先保存`pendingSafetyAlarm`；finally完成锁释放后统一追加退避和锁结果并触发一次`OnRearCraneSafetyAlarm`。

- [ ] **Step 4: finally无条件幂等释放两把锁**

共享区仍持有时：Z可能下降则跳过X；未下降且此前未尝试则尝试一次X+1000。随后无论结果都`TryRelease("SharedArea")`，再`TryRelease("RearCrane")`。

### Task 6: ST606下料应用相同策略

**Files:**
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`的`DoUnload`

- [ ] **Step 1: 增加与1号线一致的阶段、退避结果和待弹窗变量**
- [ ] **Step 2: 标记Z下降开始及安全回升完成**
- [ ] **Step 3: 保留正常提前退避顺序，失败不重复运动**
- [ ] **Step 4: finally统一释放ST606共享区锁和后天车锁并弹窗**

### Task 7: 下料文档与验证

**Files:**
- Modify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`
- Modify: `Docs/1_2号线步骤级异常与恢复审计_2026-07-04.md`

- [ ] **Step 1: 删除“下料退避失败保留共享区锁”的旧描述**
- [ ] **Step 2: 写明ST108/ST606上下料均释放两把锁、暂停并弹窗**
- [ ] **Step 3: 静态检查两个DoUnload都包含Z阶段判断、两锁释放和单次安全弹窗**
- [ ] **Step 4: 执行`dotnet build AutomaticOnlineHostComputer.csproj --no-restore`并确认0错误**
