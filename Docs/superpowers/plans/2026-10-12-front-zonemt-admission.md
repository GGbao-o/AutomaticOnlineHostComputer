# 前天车ZoneMT两项检查实施计划

按已确认的窄范围执行，本会话完成，不派发代理。

**Goal:** ZoneMT拿锁后全满释放重抢，有空位且暂停时持锁无限等待启动。

**Architecture:** 在两个ProcessCraneFrontAsync原区域try/finally内增加外层获取循环；已持锁标志和动作账本随每次获取/释放同步，不新建搬运任务。

**Tech Stack:** C#、SemaphoreSlim、Task.Delay、现有PLC状态服务、xUnit源码契约。

- [x] 新增`FrontCraneZoneMtAdmissionContractTests.cs`六项，分别覆盖两线全满分支释放后锁外读取/重抢，暂停持锁且没有业务超时/恢复复核，所有检查在原try/finally保护内并早于打号运动。
- [x] 运行新增契约，确认修改前失败。
- [x] `Line1FrontFlowEngine.cs`、`Line2FrontFlowEngine.cs`将持锁标志初始为false，在原try中按`WaitAsync→标持有→实时读架→全满释放/锁外等/continue→暂停持锁等→break→原打号`执行。
- [x] 运行新增及原前天车/异常收尾契约、主项目构建、全量回归，对照487项/8个失败基线。
- [x] 更新现行Docs，注明用户限定范围；保留Line1Front原注释，使用单独index补丁仅提交本轮变更。
- [x] 本地commit，不push，说明执行结果。
