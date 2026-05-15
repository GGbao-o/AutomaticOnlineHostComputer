# TODO — 待实现业务功能

> 当前状态：通信层完成（5协议客户端 + 6设备服务 + 8设备实时监控），手动控制面板完成（天车/机械手点动+绝对移动+速度设置）。
> 以下为尚未实现的业务调度逻辑。

---

## P0 — 主线流程引擎 ✅

- [x] 新建 `ProductionFlowEngine` 类
- [x] 全局状态机：`IDLE → LOADING → BORING → MARKING → SKEW_BED → BALANCE_CHECK → GRINDING → DONE`
- [x] 每个阶段一个 `async Task` 方法
- [x] 工件上下文 `WorkpieceContext`（版号、尺寸、长度、当前工序）
- [x] 后台 `Task` 运行，不阻塞 UI
- [x] 引擎 ↔ UI 实时联动（AdvanceStage 自动刷新 DataGrid）
- [x] D5100~D5103 脉冲规范化（2→0 脉冲模式）

## P0 — 预约机制 ✅

- [x] 天车取料前锁定目标设备（Reservation=1）
- [x] 任务完成后解锁（Reservation=0）
- [x] 调度器跳过已预约的设备

## P0 — 天车任务安全校验 ✅

- [x] 任务启动前检查天车是否故障（D5009）
- [x] 检查天车是否已有版（D5029）
- [x] 检查伺服报警（D5011）
- [x] 检查天车忙闲（D5006）

## P1 — 天车自动任务下发 ✅

- [x] MoveCraneToStationAsync：读数据库坐标 → 绝对移动 → 到位
- [x] MoveManipulatorToStationAsync：同上（跳过X轴）
- [x] D5100~D5103 脉冲规范化
- [x] 运动配置文件 Config/motion_settings.json

## P1 — 引擎 Loading 阶段接入真实调度 ✅

- [x] 机械手1 → MoveManipulatorToStationAsync(ST001)
- [x] 前天车 → MoveCraneToStationAsync(ST401/ST402)
- [x] 长度判断 → 线体分配

## P2 — 动平衡判断分流 ✅

- [x] 工件长度 > 800mm → 动平衡 → 研磨
- [x] 工件长度 ≤ 800mm → 直接研磨

## P3 — 退磁容错 ✅

- [x] 退磁失败后先充磁再退磁重试（50次上限）

## P3 — 抖动松料 ✅

- [x] ShakeReleaseAsync(count, amplitude) — X轴往复微动

## P3 — 研磨机接入引擎 Grinding 阶段 ✅

- [x] PlcGrinderService TypeA+TypeB 连接测试
- [x] ReadAllStatusAsync 状态读取

---

## 等设备 IP（阻塞）

- [ ] 双头镗握手（等 Syntec CNC IP）
- [ ] 打标机交互（等打标机 IP）
- [ ] 斜床分派（等 10 台斜床 IP）
- [x] 研磨自动分派：GrindingFlowEngine 已实现，4台研磨机 IP 192.168.2.90~93 已配置
- [ ] 货叉/料架（等三菱 MC 地址表）

## 接入设备前需确认的源码/文档差异

以下差异因设备未接入无法验证，接入时需以实际测试为准：

- [ ] **SyntecBoringAddress R6104/R6108 方向**：文档说读（CNC→PC），代码注释说写（PC→CNC）。接入双头镗后需确认
- [ ] **FanucSkewBedAddress #1102 方向**：文档说读，代码注释说写。接入沈阳斜床后需确认
- [x] **锦州斜床寄存器地址**：文档已更新为 `ModbusSkewBedAddress.cs` 的 10350~10374 地址段（原文档误用研磨机 R7301 区）
- [x] **研磨机 TypeA DO 信号**：已修正为 3 秒长信号（3000ms）。新增 `IsHeartbeatOkAsync`（PLC 心跳检测）、`IsDoorClosedAsync`（门安全检测）、`HasAnyGrindStoneAlarmAsync`（磨石厚度报警检测）。协议文档已更新为完整点位表

## 待修复的小问题

- [ ] **ManipulatorCardViewModel Mode=0** 显示"模式未知(0)"，CraneCard 已显示"待机"，需同步
- [x] **ForkService 寄存器定义**：已按正确协议修正为 4 工位模型（M900~M904 输入，M911~M914 输出），移除旧的 3 气缸错误定义
- [ ] **CraneAddress.cs 缺少 D4528/D4529/D4530**（Z 降低速预留），接入时补上常量定义

## 后面可做（纯逻辑，不依赖外部数据）

- [x] **研磨自动流程引擎**：GrindingFlowEngine 已实现完整取料/送料握手/下料流程
- [x] **研磨工件缓存**：主页面输入直径/版孔/长度 → 写入缓存 FIFO 队列
- [x] **下压急停增强**：D4523=1 同时发 D4518=2→0 急停 + 200ms 快轮询
- [ ] Z 轴快慢两段下降（数据库 safe_z_down 已有）
- [ ] 天车运动范围校验（start_x/end_x）
- [ ] 引擎测试按钮（一键跑完6阶段看日志）
- [ ] 容差分级（XY ±10, Z ±3）
- [ ] 研磨旋转架记忆

---

## 待补充的外部信息

- [ ] 货叉（三菱MC）通信地址表
- [ ] 上/中/下料架（三菱MC）通信地址表
- [ ] 小机械手1-3 IP地址（数据库默认值已有）
- [ ] 各设备实际IP地址（当前大部分标注【待补充】）
- [ ] D4002, D4007~D4009, D3105, D3125 寄存器定义确认
