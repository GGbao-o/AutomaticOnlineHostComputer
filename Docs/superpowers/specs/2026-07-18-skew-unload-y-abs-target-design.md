# 斜床下料动态 Y 绝对编码器目标设计

## 问题

后天车到斜床下料取料位时，Y 不是固定工位坐标，而是按工件顶尖对中位置计算：

```text
yPick = 数据库Y（已含工位偏移）- yOff + 天车Y偏移
```

现有 Y 微调把 `yAbsFineTune.stationTargets[craneNo][station]` 当作该动态位置的目标。该配置值实际对应基准数据库 Y 位置，导致动态取料位被错误判为超差。

例如 ST112 基准 `AbsY=-779`，动态下料位的 `yOff=170`。Crane#2 的 D5020 与显示 Y 反向，所以动态目标应为 `-779 + 170 = -609`。后续现场照片又确认 Crane#4 的 D5020 与显示 Y 同向，因此不能把该加法推广到所有后天车。

## 范围

- 覆盖 1、2 号线后天车的斜床下料取料前（Line1Rear、Line2Rear）。
- 保留共同稳定确认、最多两次微调、单次 XY 联动、超限禁止 Z 下降；Y方向改为按天车配置。
- 固定 Y 工位继续使用配置中的基准绝对值，不改变前天车、研磨天车、后天车中转架或斜床上料前行为。
- 不改异常监控/弹窗策略。

## 设计

`XAbsFineTuneHelper.VerifyAndFineTuneAsync` 接收正数几何量 `yCenterToPickOffsetMm`，默认 `0`。Y方向从 `yAbsFineTune.absolutePerDisplayDirections` 按天车编号读取。

```text
X 目标Abs = 配置基准X
Y 目标Abs = 配置中心Y - Y方向 * yCenterToPickOffsetMm
```

两条后天车的斜床下料调用都传入当前工件的正数 `yOff`，公共助手负责根据方向换算：Crane#2方向`-1`，所以中心AbsY加`yOff`；Crane#4方向`+1`，所以中心AbsY减`yOff`。

微调日志和超限异常须同时包含基准目标、动态偏移和动态目标，便于区分“公式位置正确”与“真实到位偏差”。

## 验证案例

```text
基准AbsY=-779, yOff=170
动态目标AbsY=-779+170=-609
当前AbsY=-609 => ΔAbs=0，Y 合格，允许后续 Z 下降
```

若当前动态 AbsY 与 `-609` 的差超过最大微调量，仍按原有安全逻辑禁止 Z 下降。

Crane#4验证案例：中心AbsY=`600`、`yOff=414`、方向=`+1`，动态目标为 `600-414=186`。
