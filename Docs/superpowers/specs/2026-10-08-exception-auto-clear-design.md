# 异常自动作废清零设计（斜床 / 研磨机）

## 1. 背景与目标

现场诉求：异常发生后不要让人去应急中心逐条"人工结案"，也不要去管中转架。引擎自动把**这台斜床 / 这台研磨机**清干净——清 PLC 下发数据、清内存维护的工件数据、状态回空闲，写日志/弹窗告知，之后直接启动线路就能跑下一轮。

**现场模型**：异常了，这一块板会被人工**抽走**，设备侧随即变空；下一轮派发拿的是**别的板**。

## 2. 清理范围：只有斜床和研磨机，没有例外

引擎只做一件事：

> **清这台设备的 PLC 下发数据 + 清内存状态回空闲 + 清该设备的任务牌与对应动作账本快照 + 日志/弹窗（提示"请把本块板抽走后再启动线路"）。**

**明确不碰**：

- **中转架账本**（`_rackLedger`）——不调用 `TryResolveManualReservation`，不动来源预约与来源冻结。中转架由它自己的流程管理。
- 其它设备、其它工位的状态。

**不设"已放料未闭环"例外**：按现场模型那块板同样会被抽走，设备随即变空，清掉状态与事实一致。清理后的日志/弹窗必须明确写出"请把本块板抽走"，由人工保证设备侧真的空了。

### 2.1 为什么这样清是安全的

全系统每个取料动作前都有 **X11 残留互锁**（`ReadXBitAsync(63497)`，读到 1 即"拒绝取料防止碰撞"）：

| 流程 | 互锁 |
|---|---|
| 机械手1 ST007 取料 | `Line1FrontFlowEngine.cs:1849-1873` / `Line2FrontFlowEngine.cs:1855-1879` |
| 前天车 货叉Pos3 取料 | `Line1FrontFlowEngine.cs:2423-2444` / `Line2FrontFlowEngine.cs:2426-2447` |
| 后天车 上料 | `Line1RearFlowEngine.cs:1643-1665` / `Line2RearFlowEngine.cs:1607-1629` |
| 后天车 下料 | `Line1RearFlowEngine.cs:2436-2459` / `Line2RearFlowEngine.cs:2393-2416` |
| 研磨天车 ST709 上料 | `GrindingFlowEngine.cs:1773-1796` |
| 研磨天车 下料 | `GrindingFlowEngine.cs:2432-2456` |
| 机械手2 / 机械手3 | `BalancingFlowEngine.cs:1364-1388` / `:1992-2016` |

**X11 互锁就是清空的兜底**：即使操作员还没来得及抽走板，下一轮取料也会在第一步被拦下并暂停，不会叠料、不会碰撞。因此清空逻辑里不需要再设例外。

## 3. 触发条件

> **`action.Disposition != FlowActionDisposition.Completed`**

即"这一轮没有正常走完"。`Completed` 只在每条流程唯一的一处 `action.Complete(...)` 处被置上：

| 流程 | `Complete` 位置 |
|---|---|
| 斜床上料（1号线 / 2号线） | `Line1RearFlowEngine.cs:1914` / `Line2RearFlowEngine.cs:1877` |
| 斜床下料（1号线 / 2号线） | `Line1RearFlowEngine.cs:3209` / `Line2RearFlowEngine.cs:3056` |
| 研磨上料 / 下料 | `GrindingFlowEngine.cs:2213` / `:2821` |

**不要用 `FlowActionContext.IsFinalized` 作判据**：它对自己"成功"和"人工暂停"都为真（`FlowActionContext.cs:137-140`），会把异常路径漏掉。

## 4. 清理内容

统一放在一个私有 helper 内，由每条流程的 `finally` 调用一次：

| 步骤 | 斜床（`Line1RearFlowEngine` / `Line2RearFlowEngine`） | 研磨机（`GrindingFlowEngine`） |
|---|---|---|
| 清 PLC 下发数据 | `ClearSkewDeviceEmergencyRegistersAsync(bed, ct)` | `ClearEmergencyRegistersAsync(ct)`（`PlcGrinderService.cs:431-448`） |
| 清内存工件数据 | `bed.Wp = null` | `grinder.PendingWorkpiece = null` |
| 回空闲 | `bed.St = SkewState.Idle` + 复位 `RqData/RqLoad/RqUnload/Clamped/SignalFresh/CompletionExported/WpRecoveryNeeded/X11UnloadMissWarningShown` + `Interlocked.Increment(ref bed.PollVersion)` | `grinder.State = GrinderState.Idle` + `grinder.StateChangedAt` |
| 清任务牌 | `ClearRearCraneTask()` | —（研磨用 `grinder` 自身状态展示） |
| 清动作账本快照 | `SettleManualRearLoadForEmergency(bed)` / `SettleManualRearUnloadOnCarrierForEmergency(bed)`（`Line1RearFlowEngine.cs:1225-1260`） | 对应 scope 的持件/目标待交接快照 |
| 告知 | `Console.WriteLine` + 既有安全告警回调，文案含"本轮已自动作废"与"请把本块板抽走" | 同 |

斜床的三项清零清单与既有 `EmergencyClearSkewBedAsync` 逐项一致（`Line1RearFlowEngine.cs:1334-1347`），区别只是**自动触发**且**不碰中转架**。

### 4.1 必须使用 `CancellationToken.None`

helper 在 `finally` 中执行，而异常路径上动作的 `ct` 可能已被 `CancelAndInvalidate()` 取消。若传 `ct`，清理会在第一次 `await` 抛出 `OperationCanceledException`，**掩盖原始异常**。既有 `ClearTargetGrinderPreparationAsync`（`GrindingFlowEngine.cs:1591`）就是传 `CancellationToken.None`，沿用同一做法。

### 4.2 清零后不保留人工结案

清零同时移除该设备的动作账本快照，异常监控不再挂"待人工确认"，避免出现"按钮点了没反应"。**本流程不需要任何人工结案。**

## 5. 组件与改动清单

| 组件 | 改动 |
|---|---|
| `Line1RearFlowEngine` | 清零 helper + `DoLoad` / `DoUnload` 两处 `finally` 调用 |
| `Line2RearFlowEngine` | 同上 |
| `GrindingFlowEngine` | 清零 helper + 上料 / 下料两处 `finally` 调用 |
| 现有 `finally` 告警文案 | 去掉"请先人工处理现场…执行斜床应急恢复"这类已被自动清零取代的指引，改为告知"本轮已自动作废、请把本块板抽走后启动线路" |
| 契约测试 | 新增，见第 6 节 |
| `Docs/全线业务逻辑详解.md` | 新增一节说明自动作废规则 |

**范围**：斜床上料/下料（1、2号线共 4 条）+ 研磨上料/下料（2 条）= **6 条流程**。前端机械手1/前天车、动平衡 M2/M3 不在本次范围。

## 6. 测试策略

契约测试（源码断言）锁死：

1. 清零 helper 的调用必须位于 `finally` 内，且条件表达式含 `FlowActionDisposition.Completed` 的排除。
2. 清零 helper 内所有 `await` 必须传 `CancellationToken.None`，不得使用流程 `ct`。
3. helper 内不得出现任何运动或磁铁命令（`MoveAbsoluteAsync` / `MagnetOnAsync` / `MagnetOffAsync`）。
4. helper 必须同时具备：设备清寄存器调用、`Wp = null`（或 `PendingWorkpiece = null`）、`Idle` 赋值、清任务牌、清账本快照。
5. **负向保护**：helper 内不得出现 `_rackLedger` 或 `TryResolveManualReservation`（本设计明确不管中转架）。
6. 告警/日志文案必须同时含"本轮已自动作废"与"请把本块板抽走"。
7. 不得使用 `IsFinalized` 作为清零触发判据。

## 7. 风险与边界

1. 清设备寄存器是**尽力写入、不读回核验**；失败时只把"清除失败"写进日志/告警，PLC 可能残留部分数据。
2. 清零的**前提是操作员随后真的把板抽走**。若板留在设备内而操作员直接启动，靠 X11 残留互锁拦下一轮取料；但若板停在**非磁铁**的承载面上（例如研磨下料架 ST710），X11 拦不到它。
   - ST710 放置前唯一的门禁是 MC64 `M720` bit0（`GrindingFlowEngine.cs:978-995` 派发预检、`:2687` 持件后二次确认），而该位**究竟是 ST710 的物理占用传感器还是 PLC 内部的"允许放料"时序位，仓库内无法证明**（PLC 梯形图不在仓库）。同一份协议文档对 MC65 的同类信号专门警告过：`Docs/02-通信协议手册/货叉及辅助PLC协议_三菱MC.md:311` —— "注意：M710、M720 在当前源码中都是'允许放料'，**不是'1=有板'**"。
   - 因此**现场必须确认 ST710 上确实没有残留板**后再启动线路。这一条要写进弹窗文案。
3. `ConfirmGrindingUnloadRackReadyAsync`（`GrindingFlowEngine.cs:1249-1273`）在生产代码中**没有任何调用点**，是死代码。若它原本是设计中的"放料前二次确认"，则该确认实际不存在——`:2687` 检查到 `:2743` 退磁之间没有 M720 复读。本设计不改它，但记录在案。
4. `M721` 的写入是"读整字→置位→写整字"（`MitsubishiMcClient.cs:154-191`），会把 M720 字的 bit0 一并写回。实际影响取决于 PLC 扫描周期，未确认。
5. 清零使用 `CancellationToken.None`，因此**动作已被应急取消时清理仍会执行**——与斜床应急作废语义一致。
6. 中转架账本、来源预约、来源冻结**完全不动**，仍由既有的人工入口处理。
