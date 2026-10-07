# 研磨上料 FIFO 超限派发保护

## 已确认设计

- `grinding.maxFifoCount` 是研磨上料软件 FIFO 的上限，默认值为 `2`。
- 仅在研磨天车已成功取得 `_craneLock`、并已取得 `_grindingDispatchGate` 后检查上限；此时流程尚未出队、登记 `PendingWorkpiece` 或启动 `ProcessWorkpieceAsync`。
- 如果 `CachedCount` 大于配置上限，研磨引擎设置为软暂停、向主页面发送安全告警并返回 `false`。不发送天车急停、不移动天车、不写 PLC 信号，也不删除 FIFO 数据。
- `TryDispatchLoadAsync` 的现有 `finally` 负责释放尚未转交的 `_craneLock`；超限分支不得手工重复释放。
- 恢复前需要现场确认。ST709 研磨应急每次仅删除一个软件 FIFO 队头，且不写 PLC；告警必须说明这一点。

## 不在本次范围

- 不在 `ProcessWorkpieceAsync` 内做容量判断。
- 不改变 ST010/M721、ST709/M731 或 PLC 位写入语义。
- 不以软件超限告警取消或急停已经开始的天车动作。
