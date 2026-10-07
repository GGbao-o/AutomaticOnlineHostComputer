# M2来源缓存与后天车分流等待 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** M2只在身份、路径和两侧后天车均安全时派发，并让1号线后天车在M720临时不可放料时安全等待。

**Architecture:** 派发层只决定来源是否可以启动；M817路径安全检查在派发层显式检查2、4号后天车。后天车分流层把“已成功读到M720=0”从异常改为可取消的轮询等待，读取异常仍向既有catch传播。

**Tech Stack:** .NET 8、C#、xUnit、WPF流程引擎。

---

### Task 1: M2派发门槛与路径安全

**Files:**
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs:837-880`
- Test: `AutomaticOnlineHostComputer.Tests/OperationalEvents/M2RearDispatchSafetyContractTests.cs`

- [ ] 断言M817派发条件包含`m817Cached`，且M817路径调用2、4号安全检查。
- [ ] 将M817缓存缺失改为等待日志，将M817派发改为物理信号与缓存同时成立。
- [ ] 增加M817专用双后天车安全辅助方法；ST020保留只检查4号。
- [ ] 运行定向xUnit测试。

### Task 2: M720已持板等待

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:2376-2385,3047-3057`
- Test: `AutomaticOnlineHostComputer.Tests/OperationalEvents/M2RearDispatchSafetyContractTests.cs`

- [ ] 断言M720=0没有异常文本，而是轮询等待和节流日志。
- [ ] 将M720二次读取提取为返回`bool`的读取方法；读取异常保持抛出。
- [ ] 在已有M817/M720锁和持板状态下等待读取结果为1后继续。
- [ ] 运行定向xUnit测试。

### Task 3: 业务文档与全量验证

**Files:**
- Modify: `Docs/全线业务逻辑详解.md:522,725-737`
- Modify: `Docs/03-设备交互流程/整线引擎流程总览_按代码.md:111-133`

- [ ] 更新M817缓存、双后天车安全区和M720等待的业务规则。
- [ ] 运行定向测试、全量测试、主项目构建及`git diff --check`。
