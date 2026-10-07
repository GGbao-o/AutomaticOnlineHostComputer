# 机械手1两阶段回位 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让1号线机械手到Y=11500并完成M801后立即放行货叉，同时在共享锁保护下继续回Y=1000待机；2号线直接在Y=1000放行。

**Architecture:** 用每条前端引擎独立的任务放行门闩隔离机械手完成条件与货叉状态机；机械手任务不额外创建线程，继续执行第二段回位，而主循环并行推进货叉。配置集中保存两线首次安全点和公共取板待机点。

**Tech Stack:** C# 12、.NET 8、WPF、三菱MC、Modbus TCP。

---

### Task 1: 配置语义与页面

**Files:**
- Modify: `Infrastructure/Config/MotionConfig.cs`
- Modify: `Config/motion_settings.json`
- Modify: `Presentation/ViewModels/Config/ConfigPageViewModel.cs`
- Modify: `Views/Config/ConfigPageView.xaml`

- [x] 将1号线首次安全Y设为11500、2号线设为1000。
- [x] 新增 `Manipulator1PickupStandbyY` 配置，默认1000。
- [x] 配置页面增加取板待机Y，并把原字段标题改为货叉释放安全Y。

### Task 2: 货叉放行门闩

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`

- [x] 新任务绑定 `_currentWp` 时关闭门闩。
- [x] 货叉从Idle分派时增加门闩条件和可读日志。
- [x] 退磁、Z回0、首次安全Y及M801全部成功后才打开门闩。
- [x] Z三次恢复仍失败时抛出明确异常，禁止继续执行。

### Task 3: 1号线第二段回位及共享异常

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [x] 1号线打开门闩后在同一机械手任务中继续移动到 `Manipulator1PickupStandbyY`。
- [x] 回位失败时读取实际Y；安全端点仅记录警告，中间位置或读取失败触发共享机械手告警。
- [x] 共享告警同步暂停两条线前后端并显示一个明确弹窗。

### Task 4: 文档和验证

**Files:**
- Modify: `Docs/全线业务逻辑详解.md`
- Modify: `Docs/03-设备交互流程/整线引擎流程总览_按代码.md`

- [x] 记录两阶段顺序、门闩、锁周期和异常恢复边界。
- [x] 执行 `dotnet build AutomaticOnlineHostComputer.csproj --no-restore -clp:ErrorsOnly`，结果0错误。
- [x] 执行源文件差异检查并核对R6101~R6108、M911~M914地址和顺序没有变化；生成目录由编译器刷新，不参与源文件空白检查。
