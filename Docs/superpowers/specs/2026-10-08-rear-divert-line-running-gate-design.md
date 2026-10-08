# 后天车下料分流前"本线已启动"门禁设计

## 1. 背景与目标

1/2 号线后天车完成斜床取料后，会先判断长短板，再取得两把分流位置锁（1号线 `M817`+`M720`，2号线 `M818`+`M821`），最后 XY 移动进入分流区放料。

现场问题：**如果该线路此时已被暂停（操作员刚处理完异常后暂停、或引擎因异常自动暂停），后天车仍会带着工件进入分流区**。此时线路不会继续消化这块板，天车却已经离开斜床上方的安全位置。

目标：在**取得分流锁之前**增加一道"本线已启动"的确认。未启动则保持持件与安全 Z 原地等待，上限 120 秒；超时则**作废本轮分流**，让斜床回到空闲、天车停在原地等人工处置，之后启动线路即可正常进入下一轮。

## 2. 范围

- 只改后端引擎下料分流（`DoUnload`）的入口，不涉及取料（`DoLoad`）、前端、研磨、动平衡。
- 覆盖四个分流目的地：1号线 ST019/M817、ST010/M720；2号线 ST020/M818、ST021/M820。
- 每个引擎只加 **1 个检查点**（长短板两条路径在此汇合），避免逐个目的地重复实现而漏掉一处。

## 3. 插入位置

### 3.1 为什么放在拿分流锁之前

两把分流位置锁是与**机械手2/3 和研磨天车共享**的。若在持锁状态等待线路启动，这 120 秒会无理由阻塞其它设备。放在拿锁之前，等待期间只持有后天车锁。

同时，拿锁之前抛出意味着**两把分流锁根本没被取得**，超时收尾不需要释放它们，也不存在锁泄漏窗口。

### 3.2 精确位置

| 文件 | 位置 |
|---|---|
| `Line1RearFlowEngine.DoUnload` | 分流区 `try {` 之后、`if (needsBalancing)` 之前（`bool gotM817/gotM720` 声明之下） |
| `Line2RearFlowEngine.DoUnload` | 同上结构（`bool gotM818/gotM821` 之下） |

此时的状态：天车已从斜床取料（X11=1，持件），停在斜床上方安全 Z，`ZoneTS` 已在更早的提前退避处释放，**只持有后天车锁**。

> **必须在 `try` 之内**：若在 `try` 之外抛出，外层 `finally` 不执行，后天车锁无法释放。

## 4. 判定与等待

判据是**后端引擎自身的暂停位** `_paused`：

- 主页"暂停1号线/2号线"会同时 `Pause()` 前端与后端引擎，所以它与页面按钮状态天然同步，**不需要新增跨层回调**；
- 引擎因自身异常自动暂停时同样会拦住。

等待行为：不移动天车、不退磁、不暂停、不取任何分流锁；保持持件与安全 Z；按 `grinding.pollIntervalMs` 轮询。启动后记一行日志继续进入分流区。

上限 `grinding.lineStartWaitTimeoutMs`，默认 `120000`。

## 5. 超时收尾

抛出 `LineNotStartedTimeoutException`，由 `DoUnload` 新增的专用 `catch` 分支处理：

| 步骤 | 内容 |
|---|---|
| 暂停本引擎 | `_paused = true` |
| 动作账本结论 | `MarkCommandResponseUnknown` + `PauseForManualResolution`，与其它异常分支一致 |
| 清斜床 PLC 下发数据 | 复用既有 `ClearSkewDeviceEmergencyRegistersAsync(bed, ct)`（与"斜床应急作废"同一方法，保证清除范围一致）。失败只记录，不阻断后续 |
| 内存状态回空闲 | `bed.Wp = null`、`bed.St = SkewState.Idle`，复位 `RqData/RqLoad/RqUnload/Clamped/SignalFresh/CompletionExported/WpRecoveryNeeded/X11UnloadMissWarningShown`，`PollVersion++`，`ClearRearCraneTask()`（与 `EmergencyClearSkewBedAsync` 的清除清单逐项一致） |
| 释放后天车锁 | 由外层 `finally` 完成，本分支不手工释放 |
| 告警 | 本分支**直接**调用 `OnRearCraneSafetyAlarm`，文案说明"本轮已作废、天车仍持件、下一步做什么" |

### 5.1 为什么不设置 `pendingSafetyAlarm`

外层 `finally` 在 `pendingSafetyAlarm != null` 时会追加一句固定文案，要求操作员"先人工处理现场…执行斜床应急恢复"。本场景斜床已回空闲、锁已释放，正确指令是**直接启动线路继续**，两者口径矛盾。

因此本分支不设置 `pendingSafetyAlarm`，自己发一条告警。这样：

- 只有一条告警；
- 不会出现误导性的应急恢复指引；
- 外层 `finally` 的其余职责（释放后天车锁、`_fastNextCycle`、`EndSkewOperation`）照常执行。

## 6. 天车保持不动

超时分支内**不得出现任何运动或磁铁命令**。天车保持持件与安全 Z，等待人工：

1. 放磁取下工件；
2. 把天车移回安全位置；
3. 启动线路继续。

工件身份在软件层被丢弃（`bed.Wp = null`），与"异常就是丢弃板"的既有口径一致；该板的 ERP 任务需按版号/序号重新导入。

## 7. 组件与改动清单

| 组件 | 改动 |
|---|---|
| `Service/FlowEngine/LineNotStartedTimeoutException.cs` | 新增 `public sealed` 异常，携带 `LineName`/`BedCode`/`TimeoutMs` |
| `MotionConfig.GrindingSection` | 新增 `LineStartWaitTimeoutMs { get; set; } = 120_000` |
| `Config/motion_settings.json` | `grinding.lineStartWaitTimeoutMs = 120000` |
| `Line1RearFlowEngine` | 检查点 1 处 + `EnsureLineRunningBeforeDivertAsync` helper + 专用 `catch` 分支 |
| `Line2RearFlowEngine` | 同上，线体名与锁名不同 |
| `AutomaticOnlineHostComputer.Tests/FlowEngine/RearDivertLineRunningGateContractTests.cs` | 新增契约测试 |
| `Docs/全线业务逻辑详解.md` | §10.4~§10.7 新增第 1 条；§15 新增边界 |

## 8. 测试策略

契约测试（源码断言）锁死以下不变量：

1. 检查点在分流锁标志声明之后、第一把分流锁 `AcquireAsync` 之前、第一次分流 XY 之前，且位于 `try` 块体内。
2. helper 判据读 `_paused`，上限读 `_cfg.Grinding.LineStartWaitTimeoutMs`，轮询读 `cfg.Grinding.PollIntervalMs`。
3. helper 内不得出现 `MoveAbsoluteAsync` / `MagnetOnAsync` / `MagnetOffAsync`。
4. 超时分支必须含 `ClearSkewDeviceEmergencyRegistersAsync`、`bed.St = SkewState.Idle`、`bed.Wp = null`、`ClearRearCraneTask()`。
5. 超时分支不得出现运动或磁铁命令、不得设置 `pendingSafetyAlarm`、不得出现"斜床应急恢复"字样。
6. 超时分支必须调用 `OnRearCraneSafetyAlarm` 且含"请人工放磁取下工件并把天车移回安全位置，然后启动线路继续"。
7. `new MotionConfig().Grinding.LineStartWaitTimeoutMs == 120_000`。

## 9. 风险与边界

1. 清斜床 PLC 下发数据是**尽力写入、不做读回核验**（与设备应急清零同一口径）；失败时 PLC 可能残留部分数据，需现场核对。
2. 判据只有引擎暂停位，不含天车坐标门禁、斜床状态或目标位可用性；本门禁不能替代既有的目的地二次确认与机械手2/3位置确认。
3. 超时作废只清内存状态，磁铁上可能仍吸着那块板；软件不再保留其身份，必须人工取件并从 ERP 重导任务。
4. 等待期间天车停在上方安全 Z，若现场在该位置作业需注意干涉。
