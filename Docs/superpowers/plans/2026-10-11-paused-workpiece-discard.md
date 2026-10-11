# 异常暂停工件作废 Implementation Plan

> 在当前会话逐项执行，用户已授权实施及提交；不新增代理、不push。

**Goal:** 机械手1和研磨上料动作异常暂停时删除本块来源任务，保留其他板及非暂停回队。

**Architecture:** 前端增加身份限定的暂停作废helper并串行缓存变更；研磨使用上料动作局部作废标记，在既有finally设备清理之前删除匹配FIFO残留。现有下游交接和运动握手保持。

**Tech Stack:** C#/.NET8 WPF x86、xUnit、PowerShell。

## Tasks

- [x] 新增`AutomaticOnlineHostComputer.Tests/FlowEngine/PausedWorkpieceDiscardTests.cs`：通过反射创建无设备连接的引擎队列并验证清理行为；新增暂停/非暂停分支契约。运行`dotnet test AutomaticOnlineHostComputer.Tests --filter FullyQualifiedName~PausedWorkpieceDiscard`确认旧实现失败。
- [x] 修改`Service/FlowEngine/Line1FrontFlowEngine.cs`与`Line2FrontFlowEngine.cs`：缓存入队/出队/清空同锁；`PauseAndDiscardManipulatorWorkpiece(wp)`清匹配current及完整身份缓存；暂停catch调用helper替换回队，保持交接后独立回位逻辑。
- [x] 修改`Service/FlowEngine/GrindingFlowEngine.cs`：增加`DiscardGrindingSourceWorkpiece(wp)`；上料局部`discardWorkpieceOnAbort`在实际异常暂停catch置true；去掉下压暂停回队；finally仅标记为true且动作版本有效时删除匹配身份，再复用机台清理。
- [x] 同步`docs/全线业务逻辑详解.md`、`docs/运维/异常弹窗与现场故障处置.md`、`docs/03-设备交互流程/整线引擎流程总览_按代码.md`、`docs/搬运动作状态机_异常处理与人工恢复审查说明.md`及同步记录中的当前语义。
- [x] 执行目标测试、主项目build、全量测试；如有既有失败，对照HEAD核实；`git diff --check`及提交范围核对后只提交本次文件。

## 执行结果

主项目与测试项目构建通过；新增16项回归包含普通暂停保留任务，相关五组测试38/38通过。全量426项中418通过，8项与既有配置/旧契约失败集合一致。额外同步研磨协议中的来源FIFO说明。提交仅包含本次源码、测试和文档，不push。
