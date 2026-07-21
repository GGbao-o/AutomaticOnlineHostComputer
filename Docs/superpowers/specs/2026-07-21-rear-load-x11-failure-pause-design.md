# 后天车上料 X11 三次为 0 安全暂停设计

## 目标

当后天车从中转架取板时，三次有效 X11 读取均为 0，系统必须完成退磁和 Z 轴安全收尾，按 Z 是否安全决定 ST108/ST606 的 X 退避，释放 ZoneTS、ZoneMT 和后天车锁，然后暂停对应整线并弹窗要求人工确认，禁止自动进入下一轮。

## 现状与边界

- 1号线 `Line1RearFlowEngine.DoLoad` 和 2号线 `Line2RearFlowEngine.DoLoad` 逻辑对称。
- 第三次 X11=0 已经抛异常；现有 catch 会退磁、Z 回零、恢复中转架缓存，finally 会释放锁。
- 现有暂停条件只覆盖 ST108/ST606 退避失败，普通斜床以及退避成功的三次 X11=0 会自动重试。
- 正常 X11=1、斜床握手、坐标计算、锁获取和正常退避不改变。

## 方案

在两个引擎的 `LoadHandshakeState` 增加 `X11PickupFailed`。第三次有效返回 0 时先置位，再记录现有物理事件并抛出明确异常。catch 仅对该异常链追加退磁/Z恢复结果摘要，继续执行已有缓存恢复。

finally 保持现有顺序：先按 `ZMayBeDown` 判断是否允许 ST108/ST606 X+1000 退避，再释放 ZoneTS、ZoneMT、后天车锁。三把锁释放后，如果 `X11PickupFailed` 为 true，则设置 `_paused=true`，将 X11 失败和恢复结果写入 `pendingSafetyAlarm`；已有退避失败信息只追加而不覆盖。最后复用 `OnRearCraneSafetyAlarm`，由 HomeViewModel 暂停对应线前后端并显示弹窗。

## 错误处理

- Z 回升失败：保持 `ZMayBeDown=true`，禁止 X 退避，但仍释放锁并暂停。
- 退磁失败：记录“退磁结果未知”，继续尝试 Z 回升；最终仍暂停。
- ST108/ST606 退避失败：保留已有退避失败文本，与 X11 三次失败原因合并。
- 普通斜床没有共享区退避时，仍通过 X11 失败标志触发暂停告警。
- 不使用异常消息字符串匹配判断业务分支。

## 验证

增加源码契约测试，分别验证 1/2 号线：第三次 X11=0 置位失败状态、恢复调用使用 `CancellationToken.None`、finally 在后天车锁释放后才创建暂停告警、暂停告警仍调用 `OnRearCraneSafetyAlarm`。运行全量 xUnit 和主项目构建。
