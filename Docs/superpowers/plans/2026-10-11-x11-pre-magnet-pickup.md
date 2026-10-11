# X11充磁前探测实施计划

日期：2026-10-11。依据：已确认的[设计稿](../specs/2026-10-11-x11-pre-magnet-pickup-design.md)。用户已授权实施；在当前会话顺序执行。

目标：5台天车及机械手1/2/3统一在放磁状态探测X11，三个位置为实际首次Z、+5mm、+10mm。感应成功后充磁并再次确认持件；严重动作异常暂停、作废本次任务并清所属设备软件状态。保留准入等待、普通暂停和已完成下游交接。

## 实施步骤

1. 新增`Service/FlowEngine/PickupProbeHelper.cs`及可注入的设备操作适配器。确认放磁后下降，验证补偿实际到位及安全边界，记录探测证据；通信未知、探测耗尽、充磁失败分别抛出严重取板异常。新增`PickupProbeHelperTests.cs`覆盖时序、三位置、下压、实际坐标、取消及反馈失败。
2. 扩展`FlowActionContext`的作废结案与`FlowAlarmPresentationContext`的探测记录，冻结事故上下文后清人工续搬入口，保留异常证据。
3. 为`TransferRackWorkpieceLedger`增加原子作废：核对来源、预约动作、到达代次和完整板身份；测试旧动作不得删除新到达工件。动平衡来源增加同锁保护的缓存代次及动作匹配清理。
4. 接入`Line1FrontFlowEngine.cs`、`Line2FrontFlowEngine.cs`的机械手1、货叉Pos3及打号机取回六个入口；异常不回队，清本趟任务，保护下一板前端周期。机械手接入详细告警上下文。
5. 接入`Line1RearFlowEngine.cs`、`Line2RearFlowEngine.cs`的斜床上下料四个入口；严重失败暂停，清本床及匹配来源预约；保留正常准入和动作版本校验。
6. 接入`GrindingFlowEngine.cs`的上下料两个入口；严重取料前微调/通信失败也暂停作废；禁止重放整个取板业务委托，清理前等待并行写任务退出。
7. 接入`BalancingFlowEngine.cs`的机械手2/3两个入口；详细弹窗，按动作版本和来源代次清本板，保护提前释放位置锁后新到达的板及已交接FIFO。
8. 运行构建、针对性测试和全量测试；核对已有8项基线失败与本次差异。审查14个入口和异常收尾、更新现行Docs及设计稿实施状态，显式暂存本次文件后本地commit，不push；保留用户现有配置/页面修改。

## 验证命令

```powershell
dotnet build AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj -p:WarningLevel=0
dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-build --filter "FullyQualifiedName~PickupProbe|FullyQualifiedName~TransferRackWorkpieceLedger|FullyQualifiedName~PausedWorkpieceDiscard"
dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-build --logger "trx;LogFileName=x11-pre-magnet.trx" --results-directory .planning/2026-10-11-x11-pre-magnet-implementation
git diff --check
```

软件验证不代替PLC现场联调。验收细节按设计稿第10节执行，未验证项目必须如实记录。


## 执行记录

步骤1～7代码与对应测试、步骤8软件验证及Docs同步已完成，本文随实现提交到本地Git。新增36项测试通过；全量462项/454通过/8既有失败，失败集合与426项基线相同。14入口、来源代次/版本保护、正常取消、事故冻结与最终收尾已核对；用户原4文件SHA256一致，未纳入本次提交。未push、未现场PLC联调，记录见[同步核对](../../运维/2026-10-11文档源码同步核对记录.md)末尾X11实施章节。
