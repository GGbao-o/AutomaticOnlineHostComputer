# 核对发现

基线提交：b952112。此前已有4个非Docs文件修改及业务总文档修改；非Docs文件以当前内容为不可修改基线。Docs大小写指向同一Windows目录，不重复处理。

- README含多个不存在的旧文档链接，整理日期停留8月；需重建真实导航。
- 《后天车与斜床交互流程》仍把异常描述为保留bed.Wp、保持Loading/WaitingUnload、必须应急结案；与10月8日六流程非Completed统一自动作废finally不一致。
- 《打标机_文件交互流程》误把1号线打号位写ST102（实际ST107），并写3.txt=1前置门禁，需核对源码是否真的读3.txt。
- 《全线业务逻辑详解》新增§15自动作废，但早期§7/9/10/12/13仍保留旧异常语义；需整段同步避免互相矛盾。
- 根目录task_plan/progress/findings是旧任务记录，本次不修改；本次独立计划仅写Docs内。

- 主页面EnsureNoPendingManualActionsForLine保留定义但没有调用点；启动只执行坐标零值检查，不能把注释/死代码写成有效门禁。
- 自动清斜床账本有筛选：上料仅清PauseOnCarrier/目标待交接，来源冻结不清；下料仅清PauseOnCarrier，目标待交接快照不清。研磨按上/下料FlowScope与来源/目标机台清快照。主文档“清动作账本”需要精确化。
- MotionConfig仅HomeViewModel构造Load，无FileSystemWatcher或Reload入口。配置页修改共享内存、Save持久化；外部直接改JSON不会运行中自动加载。
- 研磨当前先派上料，再派下料；协议与总览仍写下料优先，且固定Z=400与现有安全高度配置不一致。
- 天车地址手册D5000~D5029表格为空，D5009/10/5023标为未用已不符独立异常采样；需补完整真实字段。
- 两次检索路径问题：Windows rg不展开MotionConfig*.cs字面参数；MotionConfigProvider不存在。改为目录-g模式及实际MotionConfig.cs，不改源码。

- 最终顺序复核：ST108/ST606上料Completed先于finally退避，自动作废判断又早于退避；因此已完成后退避失败告警不追补清床，Machining/Wp可能保留。已同步主文档、后端分册、人工恢复、弹窗情景与核对记录。
