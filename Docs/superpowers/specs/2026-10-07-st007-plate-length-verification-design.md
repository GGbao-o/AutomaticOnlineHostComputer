# 机械手1 取料板长校验（D100 与任务板长比对）设计

> 设计日期：2026-10-07
> 适用范围：1 号线前端 `Line1FrontFlowEngine.ProcessManipulatorPickupAsync`、2 号线前端 `Line2FrontFlowEngine.ProcessManipulatorPickupAsync`
> 状态：待用户评审
>
> 修订记录：初版曾设计为"工件退回前端缓存队头 + 结构化事件"。经现场评审，改为**直接丢弃该任务身份**（不回退、不隔离），且**不接入异常监控页面**。理由见第 5 节。

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

一旦实物与缓存不一致（ERP 长度填错、上错板、版号串位），下游会拿错误长度一路决策——典型后果是把实际长板当短板**跳过动平衡直接送 ST010**，或派给装不下的斜床。而 D100 正是**取料那一刻现成可用的物理证据**，目前完全没被利用。

## 2. 目标与非目标

### 目标

1. 机械手1 从 ST007 取料前，把 D100 实测板长与任务板长比对，偏差超过容差即**拒绝取料**。
2. 不一致时**丢弃该块板的软件任务身份**：清 `_currentWp`、不回退本线缓存，使本线恢复"无在途、无待取"的干净状态。
3. 不一致时暂停本线并弹出**信息完整的红色安全弹窗**，人工据此核对实物板与 ERP 数据。
4. **不阻断后续任务的派发链路**：人工处理完实物板后，新板可正常被派发。

### 非目标（YAGNI，明确不做）

- 不校验直径、孔型、版号
- 不改动平衡 M700 来源的 MC65 `D100`（那是**直径**，非长度）
- 不改研磨 `D200` 补长逻辑
- 不改全局派发器（`HomeViewModel` 的 FrontDispatch 段）与主页任务行状态
- **不做自动纠错**：绝不把 D100 写回 `wp.Length`
- **不新增结构化事件、不接入异常监控页面**（现场评审决定：弹窗内容详细即可）
- 不改"从中转架开始"的任务路径（该路径不经过机械手1）

## 3. 判定规则

新增纯函数：

```
PlateLengthVerification.Evaluate(int measuredMm, double taskLengthMm, int toleranceMm)
    → PlateLengthVerdict { Pass, Mismatch, NotComparable }
```

> `taskLengthMm` 用 `double`：`WorkpieceCache.Length` 本身就是 `double`（ERP 可能带小数），
> 保留小数直接比较、不做取整，避免引入额外偏差。`measuredMm` 是 D100 的整数读数。

| 输入情形 | 判定 | 动作 |
|---|---|---|
| `ReadPlateLengthAsync` 抛异常 | —（不进判定函数） | **保持现状不变**：异常外抛，沿用既有收尾（回退缓存 + 暂停 + 红警） |
| `taskLength <= 0` | `NotComparable` | 仅记日志（任务板长无效，本次未校验），放行 |
| `measured <= 0` | `NotComparable` | 仅记日志（D100 读数为 0，本次未校验），放行 |
| `Abs(measured - taskLength) <= toleranceMm` | `Pass` | 放行 |
| 其它 | `Mismatch` | 抛 `PlateLengthMismatchException` → 见第 5 节 |

- 边界：偏差**正好等于容差**视为通过（"15mm 以内直接放过"）。
- 容差为 0 时：仅完全相等通过。

### 关于 `measured <= 0` 为什么是 `NotComparable` 而不是 `Mismatch`

`CenteringRackService.ReadPlateLengthAsync` 对短报文返回 0（`result.IntValues.Length > 0 ? result.IntValues[0] : 0`），不抛异常。板长真值不可能是 0，所以 0 表示"没有可用的测量值"而非"量到 0mm"。把它当 `Mismatch` 会让一次 MC 短报文直接丢弃任务并停线。故按"校验不可用"处理：放行 + 日志。

## 4. 插入点与执行顺序

判定必须插在 **D100 读取之后、`action.MarkCommandSent()` 之前**：

```
Line1FrontFlowEngine.ProcessManipulatorPickupAsync
  L1830-1858  ①.0 X11 残留检查
  L1860-1866  ①  读 D100                        ← 已有
  >>> 新增：板长判定 <<<
  L1868-1875  ②  TryGetStationCoords("ST007") / ComputePickupZ / safeZ
  L1878-1879  action.BeginStep(PreCheck) + action.MarkCommandSent()
  L1880-1882  await _manipulator1.SetAbsSpeedAsync(...)   ← 第一条 PLC 写命令

Line2FrontFlowEngine.ProcessManipulatorPickupAsync（同构，整体后移 6 行）
  L1866-1872  ①  读 D100                        ← 已有
  >>> 新增：板长判定 <<<
  L1874-1881  ②  TryGetStationCoords / ComputePickupZ / safeZ
  L1884-1885  action.BeginStep(PreCheck) + action.MarkCommandSent()
  L1886-1888  await _manipulator1.SetAbsSpeedAsync(...)   ← 第一条 PLC 写命令
```

### 为什么必须在此之前

外层 `catch (Exception ex)` 用 `action.CommandState != FlowCommandState.NotSent` 区分两类收尾：

- **`MarkCommandSent()` 之前**抛出 → 不会标记"命令响应未知"，工件身份可自由处置
- **`MarkCommandSent()` 之后**抛出 → 走"物理命令已开始 / 响应未知"分支 → 暂停但**保留在途工件与任务牌**，无法丢弃

板长不一致是**数据问题**，不是"命令响应未知"。放在 `MarkCommandSent()` 之后会把数据问题误判成通信未知，导致任务身份卡在机械手上无法丢弃。

## 5. 不一致时的处置：丢弃任务身份

抛出专用异常 `PlateLengthMismatchException`（携带 `Measured` / `Expected` / `ToleranceMm`），由外层 `catch (Exception ex)` 中**新增的专用分支**处理。

该 catch 现有的分支链是（两个引擎同构，行号取 Line1Front）：

```
L2141  if (action.CommandState != NotSent && !action.IsFinalized) { 标记响应未知 + PauseForManualResolution }   ← 非 else-if，会继续往下走
L2153  if (ex is MagnetFeedbackTimeoutException)        { 保留持件事实 + 暂停 }
L2160  else if (holdingWorkpiece && !placedOnFork)      { 清 _currentWp + 暂停 }
L2166  else if (placedOnFork)                           { 按门闩状态暂停或提示 }
L2187  else                                             { 退回缓存队尾 + 暂停 }   ← 既有通用回退
```

新分支作为 **`else if (ex is PlateLengthMismatchException mismatch)`** 插在 `else if (placedOnFork)` 之后、通用 `else` 之前。此处 `magnetOn=false`、`holdingWorkpiece=false`、`placedOnFork=false`，因此不会被前面任何分支截获。

分支动作：

1. `lock (_wpLock) { _currentWp = null; }` —— 清掉在途身份
2. **不调用 `RequeueFrontCache(wp)`** —— 这是与既有通用 `else` 分支的**唯一区别**
3. `_paused = true`
4. `OnSafetyAlarm?.Invoke(<详细文本>)` → 主页面红色安全弹窗 + 暂停本线前后端

即：**新分支 = 既有通用 `else` 分支减去回退缓存**。此处 `magnetOn` 为 false，通用分支里的退磁逻辑本来也不会执行。

### 弹窗内容要求

必须让现场不查日志就能判断怎么处理，至少包含：

- 工件身份：`wp.IdentityText`（版号 + 序号）
- **实测板长** `mismatch.Measured` mm（D100）
- **任务板长** `mismatch.Expected` mm（ERP/人工建单）
- 偏差绝对值与容差
- 来源工位 `ST007`
- 明确说明：**本任务的软件身份已丢弃**，且**实物板仍在 ST007 未被处理**
- 处置指引（见第 11 节第 1 条的操作顺序）

### 为什么"丢弃任务身份"是安全的

丢弃的是**软件任务身份**，物理板不受任何影响（未发送任何物理命令、未充磁、未移动）。此后系统状态自洽，依据有三：

1. **本线不会再去取板。** 机械手1 取板闸门要求**本线自己的缓存非空**：
   `if (inTransit == null && CachedCount > 0 && boringIdleForPickup && await _manipulatorLock.WaitAsync(0, ct))`（[Line1Front:1195](Service/FlowEngine/Line1FrontFlowEngine.cs#L1195)），锁内再校验 `rs.RequestPickup`（M800）。丢弃后本线 `CachedCount == 0` 且 `_currentWp == null` → 闸门不成立，机械手不动。
2. **另一条线也不会去取。** 另一条线的取板闸门同样要求**它自己的缓存非空**。该板的任务身份已被丢弃，两条线缓存都为空 → **没有任何线路会去碰 ST007 上这块身份不明的板**。这是本方案的核心：取板条件是"本线有任务"，而不是"识别出物理板"。
3. **全局派发器两道闸门保证不会把新任务绑到旧板上：**
   - **M800 复位门**：每次成功派发后 `_frontDispatchWaitingM800Clear = true`（[HomeViewModel:3515](Presentation/ViewModels/Home/HomeViewModel.cs#L3515)），必须**亲眼读到 M800=0** 才解除（[L3376-3380](Presentation/ViewModels/Home/HomeViewModel.cs#L3376-L3380)）。本次 M801 从未写入 → M800 一直为 1 → 门一直关着。
   - **两线前端缓存都空才派发**：`if (line1FrontCount > 0 || line2FrontCount > 0) 等待`（[L3417-3426](Presentation/ViewModels/Home/HomeViewModel.cs#L3417-L3426)）。

### 与"把任务留在本线缓存"方案的对比

若改为把任务退回本线缓存（初版设计），闸门 ② 会因 `line1FrontCount > 0` 而**永久拦住全局派发**——不只是另一条线拿不到板，而是**整条派发链路停摆**，且该缓存只能靠人工清除才能恢复。丢弃方案让派发链路保持健康，只等 M800 复位门自然解除，因此更优。

## 6. 组件

| 文件 | 职责 | 依赖 |
|---|---|---|
| `Service/FlowEngine/PlateLengthVerification.cs` | 纯静态判定：`Evaluate(measured, taskLength, toleranceMm)` → `PlateLengthVerdict` | 无（无 IO、无状态） |
| `Service/FlowEngine/PlateLengthMismatchException.cs` | 承载 `Measured` / `Expected` / `ToleranceMm` 的专用异常 | 无 |
| `Line1FrontFlowEngine` / `Line2FrontFlowEngine` | 各调用一次判定；各新增一个 catch 分支 | 上述两者 + `_cfg.Safety` |

选此形态而非内联复制的原因：判定规则单一来源，15mm 边界可用**纯单元测试**钉死（无需源码字符串断言）；两个引擎各只加约 10 行。

## 7. 配置

| 位置 | 变更 |
|---|---|
| `MotionConfig.SafetySection` | 新增 `public int PlateLengthToleranceMm { get; set; } = 15;` |
| `Config/motion_settings.json` | `safety` 段新增 `"plateLengthToleranceMm": 15` |
| `ConfigPageViewModel` + `ConfigPageView.xaml` | 新增对应字段与绑定（沿用 `PressureStopNormalPositionToleranceMm` 的既有形态与保存路径） |

不做范围校验，与仓库其它容差项一致。

## 8. 可观测性

| 情形 | 输出 |
|---|---|
| 不一致拒绝取料 | **信息完整的红色安全弹窗**（`OnSafetyAlarm`）+ `Console`/`OperationalLog` 记录 |
| `measured = 0` | `Console`/`OperationalLog` 一行日志，放行 |
| `taskLength <= 0` | `Console`/`OperationalLog` 一行日志，放行 |

不新增 `OperationalEventCatalog` 定义、不接入异常监控页面（现场评审决定）。

## 9. 测试策略

1. **纯单元测试**（重点）：`PlateLengthVerificationTests`
   - 偏差 15 → `Pass`；偏差 16 → `Mismatch`
   - 偏差 -15（实测偏小）→ `Pass`
   - `measured = 0` → `NotComparable`
   - `taskLength = 0` / 负数 → `NotComparable`
   - 容差 0 且完全相等 → `Pass`；容差 0 且相差 1 → `Mismatch`
2. **契约测试**（两个前端引擎各一）
   - 判定调用与 `throw` 必须出现在 `MarkCommandSent()` **之前**
   - 不一致分支必须清 `_currentWp`，且**该分支内不得出现 `RequeueFrontCache`**
   - 弹窗文本必须包含实测值、任务值、容差与工件身份
3. **配置契约**：`new MotionConfig().Safety.PlateLengthToleranceMm == 15`，且 JSON 能反序列化出该字段
4. 刻意**不写**"配置页 XAML 字符串断言"——那正是当前 10 个既有失败测试的来源类型

## 10. 文档同步

| 文档 | 变更 |
|---|---|
| `Docs/全线业务逻辑详解.md` §4.1 第 4 条 | 由"读取 ST007 的 D100长度用于日志和现场核对，**不以此替换任务缓存长度**"改为"读取 D100 并与任务板长比对，偏差超过容差即拒绝取料并丢弃本任务身份；不以此替换任务缓存长度" |
| 同文件 §4.2 机械手1异常状态 | 增加"板长校验不一致"条目：清 `_currentWp`、**不回退本线缓存**、暂停本线、红色弹窗、需人工处理实物板 |
| 同文件 §15 当前代码边界 | 增加"D100 读数为 0 或读失败时不执行校验；主页面任务行会停在'已入线'不自动复位" |

## 11. 现场前提与风险

1. **⚠️ M800 复位门是"必须观测到 0"，不是边沿检测。** 调度器每 1 秒轮询一次（`FrontDispatchPollInterval`，[HomeViewModel:423](Presentation/ViewModels/Home/HomeViewModel.cs#L423)），且**全仓库只有 [L3380](Presentation/ViewModels/Home/HomeViewModel.cs#L3380) 一处**会把 `_frontDispatchWaitingM800Clear` 置 false，**没有任何其它解除手段**。
   **正确的人工换板顺序（必须写入操作说明）：**
   1. 把 ST007 上的旧板拿走，**让该位置空置**
   2. 等到日志出现 `[FrontDispatch] ✓ 已观察到上一块M800复位为0, 新物理板派发门已解除`
   3. 再放上新板
   
   若"一拿一放"快于 1 秒轮询、ST007 的空窗口没被任何一次轮询撞上，复位门将**永久关闭**，除重启程序无法恢复，现场表现为日志持续刷 `⏳ 等待上一块M800复位`。本设计的安全性依赖这道门，因此该顺序是强制操作要求。
2. **D100 的写入时机未知。** 隐含假设：M800=1（板已到位）时 D100 已是本块板的有效测量值。若居中测量晚于 M800 置位，会在测量完成前读到 0 → 按 `NotComparable` 放行（不会误停，但校验会漏掉这批板）。**需现场确认 D100 与 M800 的时序。**
3. **公差来源未确认。** 15mm 是给定值。D100 是居中架测得的整数 mm，与 ERP 标称长度的正常偏差（切割公差 + 测量精度）需现场标定；容差已做成配置项便于调整。
4. **`NotComparable` 会削弱校验强度。** 若 D100 长期为 0，校验形同不存在；仅有日志。
5. **不改 D100 读取的异常语义。** 读异常仍会让本线暂停并回退缓存——这是**现有行为**，本设计不改变它。
6. **主页面任务行会停在"已入线"且按钮禁用。** 该任务已脱离全局 FIFO，因此**不影响下一个任务的派发**（现场评审已接受）。人工需按弹窗中的版号/序号从 ERP 重新导入正确任务。
7. **丢弃的是软件身份，不是物理板。** 弹窗必须明确提示实物板仍在 ST007 未被处理。
