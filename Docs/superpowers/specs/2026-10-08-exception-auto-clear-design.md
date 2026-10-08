# 异常自动作废清零设计（斜床 / 研磨）

## 1. 背景与目标

现场诉求：异常发生后不要让人去应急中心逐条"人工结案"，而是**引擎自动把这一轮作废掉**——清 PLC 下发数据、清内存维护的工件数据、设备状态回空闲，只写日志/弹窗告知，之后直接启动线路就能跑下一轮。

本设计把这条规则**只落在安全的那一侧**，并明确写出三类必须保留的状态及其理由。

## 2. 为什么不能无条件清零

系统里所有"取料"动作前都有 **X11 残留互锁**（`ReadXBitAsync(63497)`，读到 1 即 `拒绝取料防止碰撞`）：

| 流程 | 互锁 |
|---|---|
| 机械手1 ST007 取料 | `Line1FrontFlowEngine.cs:1849-1873` / `Line2FrontFlowEngine.cs:1855-1879` |
| 前天车 货叉Pos3 取料 | `Line1FrontFlowEngine.cs:2423-2444` / `Line2FrontFlowEngine.cs:2426-2447` |
| 后天车 上料 | `Line1RearFlowEngine.cs:1643-1665` / `Line2RearFlowEngine.cs:1607-1629` |
| 后天车 下料 | `Line1RearFlowEngine.cs:2436-2459` / `Line2RearFlowEngine.cs:2393-2416` |
| 研磨天车 ST709 上料 | `GrindingFlowEngine.cs:1773-1796` |
| 研磨天车 下料 | `GrindingFlowEngine.cs:2432-2456` |
| 机械手2 / 机械手3 | `BalancingFlowEngine.cs:1364-1388` / `:1992-2016` |

**这个互锁是"自动清零"安全性的全部依据**：只要板还吸在磁铁上，下一轮取料一定在第一步被拒绝并暂停——不会叠料、不会碰撞。所以"

> **板在天车磁铁上时可以放心清零；板已经离开磁铁时不可以。**

## 3. 三类必须保留的状态

### 3.1 已放到目标位、但通知未闭环 —— **保留**

- 斜床：`Line1RearFlowEngine.cs` / `Line2RearFlowEngine.cs` 下料 `placedToDestination` 为假但退磁已成功。
- 研磨：`GrindingFlowEngine.cs:2833`/`:2870` 的 `placedOnUnloadRack && unloadDoneNotified` 条件，以及 `:2869` 的原始注释：
  > 安全恢复: 不清PendingWorkpiece。根据物理位置保留等待人工确认, **避免成品在天车/下料架时软件放行新任务**。

**理由**：`M721` 是"向 ST710 放料完成"的**完成通知位**（`GrindingFlowEngine.cs:2784`，协议 `Docs/02-通信协议手册/货叉及辅助PLC协议_三菱MC.md:285-292`），不是架位占用反馈。通知没写成功意味着下游没有被通知来取走这块板。软件一旦忘记承载面上还有板，下一块就会被放到同一位置。

**这条不是可以靠"小心"绕过的**：ST710 放置前的唯一门禁是 MC64 `M720` bit0（`GrindingFlowEngine.cs:978-995` 派发预检、`:2687` 持件后二次确认），而该位**究竟是 ST710 的物理占用传感器还是 PLC 内部的"允许放料"时序位，仓库内无法证明**（PLC 梯形图不在仓库）。同一份协议文档对 MC65 的同类信号专门警告过：

> `Docs/02-通信协议手册/货叉及辅助PLC协议_三菱MC.md:311`
> 注意：M710、M720 在当前源码中都是"允许放料"，**不是"1=有板"**。

若它只是许可位，清空内存后下一趟仍会读到 M720=1 并把新成品叠到旧板上；而 `:2687` 检查到 `:2743` 退磁之间**没有再复读一次**（`:2700-2830` 区间内没有任何 M720 读取），代码里没有第二道拦截。

**因此：这一类不清。** 保留 `Unloading` + `PendingWorkpiece`，由人工确认架位后再处理。

### 3.2 来源冻结（板可能仍在来源位）—— **保留**

`Line1RearFlowEngine.cs:1229` 原注释：

> 来源冻结快照必须保留：**X11未确认时工件仍可能在中转架**。

**理由**：清掉来源预约会让下一趟任务绑到仍留在中转架上的旧板，**用错误的长度/直径参数加工实物**。这不是丢件，是把错误参数用到实物上。现有的斜床应急作废本身也不清它（`SettleManualRearLoadForEmergency` 只清持件/目标待交接，见 `Line1RearFlowEngine.cs:1225-1244`），由中转架自己的结案入口处理。

### 3.3 自动重试路径 —— **保留**

`FlowActionDisposition.WaitRetry`（尚未发送物理命令）会把工件放回队列/缓存重试，此时清零会抹掉刚退回的身份。

## 4. 清零条件

> **异常终止（非 `Completed`、非 `WaitRetry`）&& 工件已离开来源位 && 尚未交接到目标位**
> 等价于：**板此刻只可能在天车/机械手磁铁上**

成功判据：每条流程都恰好只有一处 `action.Complete(...)`（`Line1RearFlowEngine.cs:1914`/`:3209`、`Line2RearFlowEngine.cs:1877`/`:3056`、`GrindingFlowEngine.cs:2213`/`:2821`、`BalancingFlowEngine.cs:1687`/`:2385`、前端 4 处），所以 `action.Disposition == Completed` 即成功。

**注意**：`FlowActionContext.IsFinalized`（`FlowActionContext.cs:137-140`）对"成功"和"人工暂停"**都为真**，不能用作成功判据；必须用 `Disposition == Completed`。

各流程的"尚未交接到目标位"判据：

| 流程 | 判据 |
|---|---|
| 斜床下料 `DoUnload` | `!placedToDestination`（已有局部变量） |
| 斜床上料 `DoLoad` | `!plateHandedToBed`（**新增标志**，在 `DoHandshake` 调用前（`Line1RearFlowEngine.cs:1905`）置位。`bed.Wp` 在 `:1900` 就已绑定，不能用它判断） |
| 研磨上料 | 未进入"已放料 + 已通知" |
| 研磨下料 | `!(placedOnUnloadRack && unloadDoneNotified)` |

## 5. 清理内容

统一在一个私有 helper 内完成（每条流程的 `finally` 调用一次）：

| 步骤 | 斜床 | 研磨 |
|---|---|---|
| 清设备下发数据 | `ClearSkewDeviceEmergencyRegistersAsync(bed, ct)` | `ClearEmergencyRegistersAsync(ct)` |
| 清内存工件数据 | `bed.Wp = null` | `grinder.PendingWorkpiece = null` |
| 回空闲 | `bed.St = SkewState.Idle` + 复位 `RqData/RqLoad/RqUnload/Clamped/SignalFresh/CompletionExported/WpRecoveryNeeded/X11UnloadMissWarningShown` + `PollVersion++` | `grinder.State = GrinderState.Idle` + `StateChangedAt` |
| 清任务牌 | `ClearRearCraneTask()` | —（研磨用 `grinder` 自身状态展示） |
| 清动作账本快照 | `SettleManualRearLoadForEmergency(bed)` / `SettleManualRearUnloadOnCarrierForEmergency(bed)`（**保留来源冻结**） | 对应 scope 的持件/目标待交接快照 |
| 告知 | `Console.WriteLine` + 既有 `OnSafetyAlarm` / `OnRearCraneSafetyAlarm` 弹窗，文案说明"本轮已自动作废" | 同 |

### 5.1 必须用 `CancellationToken.None`

helper 在 `finally` 中执行，而异常路径上 `ct` 可能已被 `CancelAndInvalidate()` 取消。若用 `ct`，清理会在第一次 `await` 抛出 `OperationCanceledException`，**掩盖原始异常**。既有 `ClearTargetGrinderPreparationAsync`（`GrindingFlowEngine.cs:1591`）就是传 `CancellationToken.None`，沿用同一做法。

### 5.2 清零后不再要求人工结案

按现场要求，清零同时移除该动作的账本快照，异常监控不再挂"待人工确认"，避免出现"按钮点了没反应"。**唯一例外是 3.2 的来源冻结**，它仍需要走中转架自己的结案入口。

## 6. 组件与改动清单

| 组件 | 改动 |
|---|---|
| `Line1RearFlowEngine` | `DoLoad` 新增 `plateHandedToBed` 标志 + 清零 helper + 两处 `finally` 调用 |
| `Line2RearFlowEngine` | 同上（线体名与锁名不同） |
| `GrindingFlowEngine` | 清零 helper + 上料/下料 `finally` 调用；复用既有的目标机快照清除 |
| 现有 `finally` 告警文案 | 去掉"请先人工处理现场…执行斜床应急恢复"这类已被自动清零取代的指引，改为告知"本轮已自动作废、可直接启动线路" |
| 契约测试 | 新增，见第 7 节 |
| `Docs/全线业务逻辑详解.md` | 新增一节说明自动作废规则与三类保留 |

## 7. 测试策略

1. 清零调用必须位于 `finally` 内，且条件含 `Disposition == Completed` 的排除与对应位置标志的排除。
2. 清零 helper 内必须使用 `CancellationToken.None`，不得使用流程 `ct`。
3. helper 内不得出现任何运动或磁铁命令（`MoveAbsoluteAsync` / `MagnetOnAsync` / `MagnetOffAsync`）。
4. 清零后必须同时具备：设备清寄存器调用、`Wp = null` / `PendingWorkpiece = null`、`Idle` 赋值。
5. **负向保护**：不得在"已放料未闭环"的代码路径上调用清零 helper。
6. 告警文案须包含"本轮已自动作废"，且不再包含"执行斜床应急恢复"。

## 8. 风险与边界

1. **研磨 ST710 的架位互锁未经证实**（第 3.1 节）。本设计因此对该场景不清；但**现有的人工清零按钮已经有同一风险**：`EmergencyClearGrindingAsync`（`GrindingFlowEngine.cs:668-678`）、`ResetGrinderMemory`（`:485-486`）、人工结案（`:539-543`）都直接置 `Idle` + `PendingWorkpiece = null`，**既不读也不写 M720**。本设计不扩大该风险，但也不修复它。
2. **ST710 是静态下料架**（仓库内无传送带证据），成品由谁取走未确认；因此"通知未闭环"后停在架上的板必须由人工处理。
3. **`M721` 的写入是"读整字→置位→写整字"**（`MitsubishiMcClient.cs:154-191`），会把 M720 字的 bit0 一并写回。实际影响取决于 PLC 扫描周期，未确认。
4. **`ConfirmGrindingUnloadRackReadyAsync`（`GrindingFlowEngine.cs:1249-1273`）在生产代码中没有任何调用点**，是死代码；若它原本是设计中的"放料前二次确认"，则该确认实际上并不存在（`:2687` 到 `:2743` 之间没有 M720 复读）。本设计不改它，但需记录。
5. 清零使用 `CancellationToken.None`，因此**动作已被应急取消时清理仍会执行**——这与斜床应急作废的语义一致。
6. 清设备寄存器是**尽力写入、不读回核验**；失败时只把"清除失败"写进日志/告警，PLC 可能残留部分数据。
