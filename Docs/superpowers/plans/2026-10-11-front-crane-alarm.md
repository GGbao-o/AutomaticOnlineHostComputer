# 前天车异常详情实施计划

在当前会话按已获批准的范围直接实施，沿用现有统一窗口。

**Goal:** 将1/2号线前天车原有安全告警接入详情窗，业务语句保持不变。

**Architecture:** 在现有FlowAlarmPresentationContext中补充可冻结的阶段来源/目标及显示步骤；FrontCraneAlarmDispatchScope仅观察外层调度锁收尾。两个前端引擎仅新增展示语句，HomeViewModel及现场只读采样入口复用。

**Tech Stack:** C#、.NET 8、WPF、xUnit。

## 1. 展示模型与测试

- [x] 新增 `AutomaticOnlineHostComputer.Tests/Presentation/FrontCraneAlarmPresentationTests.cs`：先验证阶段冻结、未知目标、文件握手步骤、调度释放与显示失败隔离。
- [x] 修改 `Service/Alarms/SafetyAlarmDetails.cs`、`SafetyAlarmFormatter.cs`：追加阶段和显示步骤，补ST713/ST714及ST107/ST502名称。
- [x] 修改 `Service/FlowEngine/FlowAlarmPresentationContext.cs`、新增 `FrontCraneAlarmDispatchScope.cs`，提供只修改展示对象的API：

```csharp
FlowAlarmPresentationContext.BeginFront(action, wp, "1号线", 1);
FlowAlarmPresentationContext.RecordTransferPhase("ST107", "中转架（尚未选择）", "打号机取回");
using var alarmDispatch = FrontCraneAlarmDispatchScope.Begin();
FrontCraneAlarmDispatchScope.RecordReleased(); // 原_craneFrontLock.Release成功返回之后
```

## 2. 1/2号线接入

- [x] 在两个 `Line*FrontFlowEngine.cs` 的调度、ProcessCraneFrontAsync、MarkingHandshakeAsync及PlaceOnTransferRackAsync中追加显示语句。
- [x] 只在最终catch保存失败，不在内部重试catch提前冻结；原OnSafetyAlarm触发条件不变。
- [x] 记录原队列回队及中转架缓存通知实际结果；只读描述当前货叉阶段和_currentWp，不清空、不恢复。
- [x] 去除新增展示语句后与HEAD逐字比较，核对生产代码无其他变化。

## 3. 验证与提交

- [x] 定向测试：`dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter "FrontCraneAlarmPresentation|SafetyAlarm|CraneAlarmSnapshotReader|LegacyAlarmOrderingContractTests" -p:WarningLevel=0`。
- [x] 完整回归，与现有400项测试的10项原有失败对比；Release构建；无真实设备的WPF布局验证。
- [x] 检查差异和限定提交文件；执行commit，不push。

## 验证记录（2026-10-11）

- 原基线：`efc732b`。两份前天车引擎去除新增展示语句后，与基线源码逐字一致；没有更改原指令、异常裁决、暂停/重试、队列、业务缓存或生产锁语句。
- 定向测试：38/38通过。新增9个前天车展示测试用例，另新增1个无设备WPF前天车布局用例；原后天车布局用例保留。
- 完整回归：410项，400通过、10失败。通过TRX集合比较，10项失败与此前`output/alarm-dialog-verification/safety-alarm-full.trx`完全一致，无新增失败。
- Release构建成功，0错误（命令使用`-p:WarningLevel=0`）。`git diff --check`通过。
- 已渲染并检查模拟前天车窗口；测试使用模拟读取，未启动完整应用、连接或操作真实设备。真实现场联调尚未执行。
- 展示中区分文件/并行动作失败步骤与最后业务运动记录；ZoneMT/ZoneTS仅显示动作账本登记，调度锁仅在原Release成功后显示已释放，不用信号量计数推测归属。
- 结果文件位于`output/front-crane-alarm-verification/`，不随代码提交。提交限定为本计划、设计文档、两个测试文件及六个生产文件；用户其他已有修改不纳入。仅commit，不push。
