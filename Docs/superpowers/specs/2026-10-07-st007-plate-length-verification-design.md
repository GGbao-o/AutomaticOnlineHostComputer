# 机械手1 取料板长校验（D100 与任务板长比对）设计

> 设计日期：2026-10-07
> 适用范围：1 号线前端 `Line1FrontFlowEngine.ProcessManipulatorPickupAsync`、2 号线前端 `Line2FrontFlowEngine.ProcessManipulatorPickupAsync`
> 状态：待用户评审

## 1. 背景

总上料架（MC63 / 192.168.2.63）的 **D100** 是居中架在取料位 ST007 测得的板长（mm，`ushort`）。两条前端引擎的机械手1 取料流程**已经在读它**，但**只打印日志**：

```csharp
// ① 读 D100 板长 — 仅日志输出，不影响流程
int plateLen = 0;
if (_rackSvc != null)
{
    plateLen = await _rackSvc.ReadPlateLengthAsync(ct);
    Console.WriteLine($"[Line1Front] [机械手1] ① D100板长={plateLen}mm");
}
```

`plateLen` 在两个引擎里各只出现三次（声明、赋值、打印），全仓库没有任何 `plateLen` 比较。D100 的其它去向也只有页面显示（`ds.RackPlateLength`）与派发日志。

而任务板长 `WorkpieceCache.Length` 来自 ERP / 人工建单，是**整条下游链路的判决依据**：

- 全局分线：`skewBed.maxWorkpieceLengthMm` 决定哪条线可加工
- 斜床上料候选：长度必须 ≤ 该床最大长度
- 下料分流：`wp.Length >= 800 || ForceBalancing` → ST019/ST020（动平衡），否则 ST010/ST021
- 研磨上料：`grinding.maxWorkpieceLengthMm[站号] >= 工件长度` 才派发

因此一旦实物与缓存不一致（ERP 长度填错、上错板、版号串位），下游会拿错误长度一路决策——典型后果是把实际长板当短板**跳过动平衡直接送 ST010**，或派给装不下的斜床。而 D100 正是**取料那一刻现成可用的物理证据**，目前完全没被利用。

## 2. 目标与非目标

### 目标

1. 机械手1 从 ST007 取料前，把 D100 实测板长与任务板长比对，偏差超过容差即**拒绝取料**。
2. 不一致时**不丢失工件身份**、不移动机械手、不退磁。
3. 不一致可被人工快速判定与处置，且**人工未处理前无法靠再次点启动绕过**。
4. 校验本身可观测：异常监控页能看到实测值、任务值、偏差、容差。

### 非目标（YAGNI，明确不做）

- 不校验直径、孔型、版号
- 不改动平衡 M700 来源的 MC65 `D100`（那是**直径**，非长度）
- 不改研磨 `D200` 补长逻辑
- 不改全局派发器（`HomeViewModel` 的 FrontDispatch 段）
- **不做自动纠错**：绝不把 D100 写回 `wp.Length`
- 不改"从中转架开始"的任务路径（该路径不经过机械手1）

## 3. 判定规则

新增纯函数：

```
PlateLengthVerification.Evaluate(int measured, int taskLength, int toleranceMm)
    → PlateLengthVerdict { Pass, Mismatch, NotComparable }
```

| 输入情形 | 判定 | 动作 |
|---|---|---|
| `ReadPlateLengthAsync` 抛异常 | —（不进判定函数） | **保持现状不变**：异常外抛，沿用既有收尾（回退缓存 + 暂停 + 红警） |
| `taskLength <= 0` | `NotComparable` | 跳过校验，放行；上报黄色事件（原因=任务板长无效） |
| `measured <= 0` | `NotComparable` | 放行取料；上报黄色事件（原因=D100 读数为 0） |
| `Abs(measured - taskLength) <= toleranceMm` | `Pass` | 放行 |
| 其它 | `Mismatch` | 抛 `PlateLengthMismatchException` → 见第 5 节 |

- 边界：偏差**正好等于容差**视为通过（"15mm 以内直接放过"）。
- 容差为 0 时：仅完全相等通过。
- 负数输入（`measured < 0` 或 `taskLength < 0`）：`taskLength <= 0` 或 `measured <= 0` 已归入 `NotComparable`。

### 关于 `measured <= 0` 为什么是 `NotComparable` 而不是 `Mismatch`

`CenteringRackService.ReadPlateLengthAsync` 对短报文返回 0（`result.IntValues.Length > 0 ? result.IntValues[0] : 0`），不抛异常。板长真值不可能是 0，所以 0 表示"没有可用的测量值"而非"量到 0mm"。把它当 `Mismatch` 会让一次 MC 短报文直接停线。故按"校验不可用"处理：放行 + 黄色告警，保留可观测性。

## 4. 插入点与执行顺序

判定必须插在 **D100 读取之后、`action.MarkCommandSent()` 之前**：

```
Line1FrontFlowEngine.ProcessManipulatorPickupAsync
  L1830-1858  ①.0 X11 残留检查
  L1860-1866  ①  读 D100                        ← 已有
  >>> 新增：板长判定 <<<
  L1868-1874  ②  TryGetStationCoords("ST007") / ComputePickupZ
  L1876-1877  action.BeginStep(PreCheck) + action.MarkCommandSent()
  L1878-1881  await _manipulator1.SetAbsSpeedAsync(...)   ← 第一条 PLC 写命令
```

`Line2FrontFlowEngine` 同构（D100 读取在 L1866-1872）。

### 为什么必须在此之前

外层 `catch (Exception ex)` 用 `action.CommandState != FlowCommandState.NotSent` 区分两类收尾：

- **`MarkCommandSent()` 之前**抛出 → 走"尚未下发物理命令"分支 → 可安全回退工件缓存 + 暂停
- **`MarkCommandSent()` 之后**抛出 → 走"物理命令已开始 / 响应未知"分支 → 暂停但**保留在途工件与任务牌**，不回退

板长不一致是**数据问题**，不是"命令响应未知"。放在 `MarkCommandSent()` 之后会把数据问题误判成通信未知，导致工件身份卡在机械手上无法回退。

## 5. 不一致时的处置

抛出专用异常 `PlateLengthMismatchException`（携带 `Measured` / `Expected` / `ToleranceMm`），由外层 `catch (Exception ex)` 中**新增的专用分支**处理。

该 catch 现有的分支链是（两个引擎同构）：

```
L2141  if (action.CommandState != NotSent && !action.IsFinalized) { 标记响应未知 + PauseForManualResolution }   ← 非 else-if，会继续往下走
L2153  if (ex is MagnetFeedbackTimeoutException)        { 保留持件事实 + 暂停 }
L2160  else if (holdingWorkpiece && !placedOnFork)      { 清 _currentWp + 暂停 }
L2166  else if (placedOnFork)                           { 按门闩状态暂停或提示 }
L2187  else                                             { 退回缓存队尾 + 暂停 }   ← 既有通用回退
```

新分支作为 **`else if (ex is PlateLengthMismatchException lengthMismatch)`** 插在 `else if (placedOnFork)` 之后、通用 `else` 之前。此处 `magnetOn=false`、`holdingWorkpiece=false`、`placedOnFork=false`，因此不会被前面任何分支截获。

分支动作四件：

1. `_paused = true`
2. **工件退回本线前端缓存队头**（新增方法 `RequeueFrontCacheHead`）
3. 上报结构化事件 `PLATE_LENGTH_MISMATCH`（含实测、任务、偏差、容差）
4. `OnSafetyAlarm?.Invoke(...)` 红色安全弹窗，文本含四个数值并要求人工核对上料板与任务数据

### 为什么必须退到队头（而不是复用既有 `RequeueFrontCache`）

既有 `RequeueFrontCache` 是**追加队尾**，注释写明"这里追加到队尾, 牺牲极少见异常场景下的顺序"。

但本场景物理事实不同：**那块板仍在 ST007**，M800 仍为 1，它的身份就是当前任务。若退到**队尾**，恢复后队头变成"下一块任务"，而 ST007 上站着的仍是不匹配的旧板——程序会拿新任务的长度去取旧板，把"长度不符"升级为"张冠李戴"，比原问题更危险。

退到**队头**则形成硬门禁：人工不修正数据（或把板取走）时，一恢复就立刻再次触发同一校验失败并再次暂停，无法靠反复点启动绕过。这与"线路恢复需要人工结案"的既有安全文化一致。

## 6. 组件

| 文件 | 职责 | 依赖 |
|---|---|---|
| `Service/FlowEngine/PlateLengthVerification.cs` | 纯静态判定：`Evaluate(measured, taskLength, toleranceMm)` → `PlateLengthVerdict` | 无（无 IO、无状态） |
| `Service/FlowEngine/PlateLengthMismatchException.cs` | 承载 `Measured` / `Expected` / `ToleranceMm` 的专用异常 | 无 |
| `Line1FrontFlowEngine` / `Line2FrontFlowEngine` | 各调用一次判定；各新增一个 catch 分支与一个队头回退方法 | 上述两者 + `_cfg.Safety` + `_exceptionReporter` |

选此形态而非内联复制的原因：判定规则单一来源，15mm 边界可用**纯单元测试**钉死（无需源码字符串断言）；两个引擎各只加约 10 行。选此形态而非"重共享组件"的原因：读信号、判定、上报、抛异常若打成一件，就需要持有 `_rackSvc`/reporter/站点上下文，把"读信号"与"业务决策"耦合，与仓库既有 `XAbsFineTuneHelper`、`CraneZeroPositionGuard` 那种"只做一件纯事"的粒度不符。

## 7. 配置

| 位置 | 变更 |
|---|---|
| `MotionConfig.SafetySection` | 新增 `public int PlateLengthToleranceMm { get; set; } = 15;` |
| `Config/motion_settings.json` | `safety` 段新增 `"plateLengthToleranceMm": 15` |
| `ConfigPageViewModel` + `ConfigPageView.xaml` | 新增对应字段与绑定（沿用 `PressureStopNormalPositionToleranceMm` 的既有形态与保存路径） |

不做范围校验，与仓库其它容差项（`absMove.tolerance`、`safety.pressureStopNormalPositionToleranceMm`、`absFineTuneVerification.stableRangeMm`）一致。

## 8. 可观测性

在 `OperationalEventCatalog` 新增两条定义（`Define` 签名为 `code, title, severity, category, required, forbidden`）：

```csharp
Define("PLATE_LENGTH_MISMATCH", "板长与任务不符", OperationalEventSeverity.Error, OperationalEventCategory.Safety,
    "核对D100实测板长、任务板长、偏差和容差，并现场确认ST007上实际是哪一块板、对应哪一条任务数据。",
    "禁止在未纠正任务数据或未移走实物板的情况下继续取料；禁止把D100实测值直接写回任务板长。"),

Define("PLATE_LENGTH_NOT_VERIFIED", "板长校验不可用", OperationalEventSeverity.Warning, OperationalEventCategory.Safety,
    "核对D100本次读到0的原因（居中测量未完成/短报文）与任务板长是否有效。",
    "禁止把“未校验”表述为“校验通过”；禁止据此推断实物板长与任务一致。"),
```

| 情形 | 事件码 | 级别 |
|---|---|---|
| 偏差超容差，拒绝取料 | `PLATE_LENGTH_MISMATCH` | `Error` + 红色安全弹窗 |
| `measured = 0`（D100 读数为 0，短报文） | `PLATE_LENGTH_NOT_VERIFIED` | `Warning`，放行取料 |
| `taskLength <= 0`（任务板长无效） | `PLATE_LENGTH_NOT_VERIFIED` | `Warning`，放行取料 |

`PLATE_LENGTH_NOT_VERIFIED` 的 `required` 文本中需带上区分两类原因的具体说明（D100 读数为 0 / 任务板长无效）。

> 说明：`LEGACY_WARNING` 只是 `AttentionEventCenter` 把既有 `AttentionEventKind.Warning` 映射进结构化库时用的代码，不是供引擎直接发事件的通道；引擎侧统一通过 `OperationalEventContextFactory.TryReportPhysical` 上报，事件码作为其第一个业务参数传入。因此这里新增专用代码而不复用 `LEGACY_WARNING`。

## 9. 测试策略

1. **纯单元测试**（重点）：`PlateLengthVerificationTests`
   - 偏差 15 → `Pass`；偏差 16 → `Mismatch`
   - 偏差 -15（实测偏小）→ `Pass`
   - `measured = 0` → `NotComparable`
   - `taskLength = 0` / 负数 → `NotComparable`
   - 容差 0 且完全相等 → `Pass`；容差 0 且相差 1 → `Mismatch`
2. **契约测试**（两个前端引擎各一）：判定调用与 `throw` 必须出现在 `MarkCommandSent()` **之前**；不一致分支必须调用队头回退方法（且不得调用队尾版本）；必须上报 `PLATE_LENGTH_MISMATCH`
3. **配置契约**：`new MotionConfig().Safety.PlateLengthToleranceMm == 15`，且 JSON 能反序列化出该字段
4. 刻意**不写**"配置页 XAML 字符串断言"——那正是当前 10 个既有失败测试的来源类型

## 10. 文档同步

| 文档 | 变更 |
|---|---|
| `Docs/全线业务逻辑详解.md` §4.1 第 4 条 | 由"读取 ST007 的 D100长度用于日志和现场核对，**不以此替换任务缓存长度**"改为"读取 D100 并与任务板长比对，偏差超过容差即拒绝取料；不以此替换任务缓存长度" |
| 同文件 §4.2 机械手1异常状态 | 增加"板长校验不一致"条目：暂停本线、工件退回前端缓存队头、红色弹窗、人工确认前无法恢复 |
| 同文件 §15 当前代码边界 | 增加"D100 读数为 0 或读失败时不执行校验" |

## 11. 风险与现场前提

1. **D100 的写入时机未知。** 本设计隐含假设：M800=1（板已到位）时 D100 已是本块板的有效测量值。若居中测量晚于 M800 置位，会在测量完成前读到 0 → 按 `NotComparable` 放行，不会误停，但校验会漏掉这批板。**需现场确认 D100 与 M800 的时序。**
2. **公差来源未确认。** 15mm 是用户给定值。D100 是居中架测得的整数 mm，与 ERP 标称长度的正常偏差（切割公差 + 测量精度）需现场标定；容差已做成配置项，便于调整。
3. **`NotComparable` 会削弱校验强度。** 若 D100 长期为 0，校验形同不存在；黄色告警用于暴露这种情况。
4. **不改 D100 读取的异常语义。** 读异常仍会让本线暂停并回退缓存——这是**现有行为**，本设计不改变它。
