# 后天车上料 X11 三次失败最终结构化事件实施计划

> **For agentic workers:** 本计划由主代理在当前工作区按步骤执行；所有步骤保持原业务动作不变。

**Goal:** 让1/2号线后天车上料X11三次有效为0在finally收尾完成后只产生一条包含最终恢复、锁和暂停证据的结构化事件。

**Architecture:** 复用`CRANE_X11_NOT_CONFIRMED`和现有`OperationalEventContextFactory`，将Reporter调用从第三次X11失败点移动到finally完成并触发安全告警之后。通过物理周期tracker和局部最终化快照承载Z、退磁、退避、锁和暂停结果；X11专用回调复用原暂停和弹窗但跳过重复Legacy事件，不改变原设备动作。

**Tech Stack:** C#、.NET 8、WPF、xUnit、现有OperationalEvent模型和Reporter。

---

### Task 1: 建立最终化证据模型

**Files:**
- Modify: `Service/FlowEngine/OperationalEventContextFactory.cs`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`
- Test: `AutomaticOnlineHostComputer.Tests/OperationalEvents/RearLoadX11FailureContractTests.cs`

- [x] 为最终事件增加一个只读最终化输入记录，字段包含退避状态、三把锁释放状态、后端暂停状态、告警回调已执行状态；未知和不适用状态使用现有EvidenceValue。
- [x] 在两条后天车上料的`LoadHandshakeState`中增加最终化快照字段，所有赋值只紧邻现有命令返回或锁释放调用。
- [x] 在Z回安全命令前后调用tracker已有的`BeginSafeZReturn`/`ConfirmSafeZ`，不改变命令和取消令牌。
- [x] 扩展契约测试，断言最终化输入在`OnRearCraneSafetyAlarm`之后才触发Reporter，且1/2号线代码对称。

### Task 2: 将X11事件移动到最终收尾

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`
- Test: `AutomaticOnlineHostComputer.Tests/OperationalEvents/RearLoadX11FailureContractTests.cs`

- [x] 删除第三次X11=0位置的Reporter调用，但保留`X11PickupFailed=true`和原异常文本。
- [x] 在finally完成锁释放、设置`_paused=true`并调用安全告警回调后调用一次最终事件构造方法。
- [x] 保证Reporter调用包在现有旁路隔离边界内；任何监控异常都不能跳过`EndSkewOperation`或改变原finally行为。
- [x] 断言方法中只有一次`CRANE_X11_NOT_CONFIRMED`调用，并且调用位置晚于`OnRearCraneSafetyAlarm`。

### Task 3: 完善最终事件字段和详情

**Files:**
- Modify: `Service/FlowEngine/OperationalEventContextFactory.cs`
- Test: `AutomaticOnlineHostComputer.Tests/OperationalEvents/OperationalEventFormatterTests.cs`
- Test: `AutomaticOnlineHostComputer.Tests/OperationalEvents/OperationalEventReporterTests.cs`

- [x] 生成`RecoveryEvidence`，逐步展示退磁、Z回安全和X退避的最终状态。
- [x] 生成`LockEvidence`，展示ZoneTS、ZoneMT和RearCrane最终释放状态及原因。
- [x] 将最终暂停和安全告警回调执行事实写入BusinessState/Recovery，不把回调执行推断为另一条线的实时暂停确认。
- [x] 保留板号、序号、直径、长度、来源、目标和最后确认位置；字段缺失输出Unknown及原因。
- [x] 测试成功、失败、跳过和未知组合，确认详情包含最终结果而不是只显示X11根因。

### Task 4: 更新业务文档与使用手册

**Files:**
- Modify: `Docs/03-设备交互流程/后天车与斜床交互流程.md`
- Modify: `Docs/异常监控页面使用手册.md`
- Modify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`

- [x] 明确X11三次失败事件在finally收尾完成后生成一次，次数为1。
- [x] 列出事件详情中的最终退磁、Z、退避、锁和暂停字段，并说明Unknown含义。
- [x] 删除任何暗示该事件会自动重试或只显示初始X11值的旧表述。

### Task 5: 验证

**Files:**
- No additional files.

- [x] Run `dotnet test AutomaticOnlineHostComputer.sln --no-restore` and require all tests pass.
- [x] Run `dotnet build AutomaticOnlineHostComputer.csproj --no-restore` and require zero warnings and zero errors.
- [x] Run `git diff --check` and inspect the final diff to verify only monitoring evidence, tests, comments and documents changed.
