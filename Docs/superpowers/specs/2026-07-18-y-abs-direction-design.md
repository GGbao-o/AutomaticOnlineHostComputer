# 天车 Y 绝对编码器反向微调设计

## 背景与证据

在 `ST105` 的微调日志中，显示 Y 从 `652` 变为 `665`（`+13`），而 D5020 的绝对编码器从 `-764` 变为 `-777`（`-13`）。因此，Y 显示坐标与 Y 绝对编码器的增量方向相反；X 轴仍为同向。

以目标 `AbsY=-751`、当前 `AbsY=-764`、显示 `Y=652` 为例：

- `DeltaAbs = TargetAbs - CurrentAbs = +13`；
- 旧公式把显示目标设为 `652 + 13 = 665`，使绝对值变成 `-777`，偏差扩大；
- 新公式把显示目标设为 `652 - 13 = 639`，预期使绝对值增加到 `-751`。

## 范围

- 仅修正 `XAbsFineTuneHelper` 中 Y 轴的显示坐标—绝对编码器方向映射。
- X 轴继续采用原同向公式。
- 保持现有的共同稳定采样、最多两次微调、超限禁止 Z 下降、单次 XY 联动移动和异常处理策略不变。
- 不改变本次异常是否弹窗或流程是否暂停的策略。

## 方案

为每个参与微调的轴定义 `AbsolutePerDisplayDirection`：X 为 `+1`，Y 为 `-1`。

移动计算统一为：

```text
deltaAbs     = targetAbs - currentAbs
deltaDisplay = deltaAbs * AbsolutePerDisplayDirection
targetDisplay = currentDisplay + deltaDisplay
```

移动反馈统一按预期绝对值位移校验：

```text
displayMove     = displayAfter - displayBefore
absMove         = absAfter - absBefore
expectedAbsMove = displayMove * AbsolutePerDisplayDirection
followError     = absMove - expectedAbsMove
```

当 `followError` 超过现有允许误差时，仍按原安全逻辑阻止继续微调和 Z 下降。

## 验证

- 静态检查 X 的 `deltaAbs=+13` 仍生成 `deltaDisplay=+13`。
- 静态检查 Y 的 `deltaAbs=+13` 生成 `deltaDisplay=-13`。
- 构建主工程，并执行 `git diff --check`。
