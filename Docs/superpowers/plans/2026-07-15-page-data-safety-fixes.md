# 页面数据可信度与操作防护实施计划

> **执行方式：** 当前会话内逐项实施；遵照用户要求不创建 Git 提交。

**目标：** 用小范围、可隔离的改动修复六项页面问题，同时保证新增状态不参与自动线控制决策，不增加设备通信，不改变动作顺序、握手、锁、缓存和超时。

**技术边界：** C# / .NET WPF；复用现有引擎内存快照和页面刷新；统一以 5 秒判断展示快照是否过期；所有应急底层方法保持原样。

---

## Task 1：修正只读按钮与人工任务校验

**文件：**
- `Presentation/ViewModels/Home/GrinderCardViewModel.cs`
- `Views/Overview/EquipmentOverviewView.xaml`
- `Presentation/ViewModels/Home/AddTaskDialogViewModel.cs`

- [x] 将研磨机“急停/清报警”改为始终不可执行的命令，按钮文案和 Tooltip 明确“未配置/只读”，不写虚构 PLC 地址。
- [x] 人工任务补齐长度、直径、堵孔、左右堵厚的有限数和正数校验，规则与 ERP 导入一致。
- [x] 静态检查确认没有新增设备写调用。

## Task 2：增加展示快照时间并修正诊断映射

**文件：**
- `Service/FlowEngine/FlowStatusDisplaySnapshots.cs`
- `Service/FlowEngine/Line1FrontFlowEngine.cs`
- `Service/FlowEngine/Line2FrontFlowEngine.cs`
- `Service/FlowEngine/Line1RearFlowEngine.cs`
- `Service/FlowEngine/Line2RearFlowEngine.cs`
- `Presentation/ViewModels/Home/HomeViewModel.cs`

- [x] 增加只用于展示的 `SnapshotAtUtc`，由现有循环完成状态维护时赋值。
- [x] 前端暂停/恢复同步维护只读暂停标记，不触发额外读取。
- [x] 共享 5 秒展示新鲜度判断；中转架暂停或过期时不把旧物理位当作当前证据。
- [x] 运行诊断区分未启动、暂停/最后已知、数据过期、已连接和断开。
- [x] 修正 ST111=`Skew3Connected`、ST110=`Skew4Connected`。
- [x] 共享 ST001/ST002 从两条线选择最新且有效的快照。

## Task 3：保留 M2/M3 危险异常下的显示身份

**文件：**
- `Service/FlowEngine/BalancingFlowEngine.cs`

- [x] M2/M3 动作增加局部 `keepDisplayForManualConfirmation`，仅控制 Tooltip/在制页清理。
- [x] 机械手持件、已放目标但握手/缓存未闭环时保留工件身份和人工确认说明。
- [x] 正常完成和明确未持件失败仍清除；应急成功按动作版本清除。
- [x] 核对动作版本保护，保证旧动作不能清理新动作显示。

## Task 4：应急中心统一页面级防重入

**文件：**
- `Views/Home/Dialogs/EmergencyCenterDialog.xaml`
- `Views/Home/Dialogs/EmergencyCenterDialog.xaml.cs`

- [x] 用统一原子门闩覆盖前端、斜床、动平衡和研磨应急。
- [x] 执行前保存线体/设备目标到局部变量，后续调用、记录和刷新都使用该局部值。
- [x] 执行期间禁用所有应急操作按钮及目标选择控件，`finally` 中恢复并释放门闩。
- [x] 不改变 6 秒等待、设备清除、仅清软件确认和底层恢复逻辑。

## Task 5：更新运维文档

**文件：**
- `Docs/整线技术手册.md`
- `Docs/日志审查与故障定位手册.md`
- `Docs/03-设备交互流程/整线引擎流程总览_按代码.md`

- [x] 说明按钮能力边界、5 秒快照语义、最后已知值、M2/M3 人工确认显示和应急防重入。
- [x] 明确所有新增快照字段只用于页面，不是控制条件。

## Task 6：审核与验证

- [x] 用 `rg` 检查新增展示字段没有进入派发、运动、握手、锁或缓存决策。
- [x] 用 `rg` 检查显示保留变量只影响 `ClearM2Display/ClearM3Display`。
- [x] 运行 `dotnet build AutomaticOnlineHostComputer.csproj --no-restore --nologo --consoleloggerparameters:\"ErrorsOnly;Summary\"`，要求 0 错误。
- [x] 运行限定本次源文件的 `git diff --check`，逐文件审阅生产代码差异；不提交 Git。
