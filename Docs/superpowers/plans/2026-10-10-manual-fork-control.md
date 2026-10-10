# 货叉手动运动控制 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 在手动操作页增加用户确认的线路选择、状态刷新和M911～M914四个动作，验证后仅提交Git。

**Architecture:** 独立ForkManualControlViewModel持有两条线的懒创建客户端，通过薄接口复用ForkService。HomeViewModel只提供ST711/ST712工位资料，XAML绑定独立控制区；不调用生产引擎和业务队列。

**Tech Stack:** .NET 8、WPF、C#、xUnit、现有MC 1E ForkService。

---

### Task 1: 行为测试与独立控制对象

**Files:**
- Create: `AutomaticOnlineHostComputer.Tests/Presentation/ForkManualControlViewModelTests.cs`
- Create: `Presentation/ViewModels/Home/IForkManualDevice.cs`
- Create: `Presentation/ViewModels/Home/ForkManualControlViewModel.cs`

- [x] 写可控异步假设备测试：ST711/ST712分别连接实际IP/端口，四个动作各调用一次对应方法；刷新只读；切换清除反馈；通信期间拒绝切换及并发指令；发送失败不重放；写成功但读失败保留“已发送”。
  测试入口：`await vm.GoStandbyAsync(); Assert.Equal(1, fake.Commands.Count); Assert.Null(vm.HasPlate);`。
- [x] 运行 `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~ForkManualControlViewModelTests --verbosity minimal`，先确认新类型尚未实现导致编译失败。
- [x] 实现接口、ForkService适配器和ViewModel。使用 `Interlocked.CompareExchange` 串行化一个请求；请求开始固定线路及配置；按线路复用服务，配置改变后再替换；退出时取消请求并释放手动连接。
- [x] 同命令重跑，确认新增测试全部通过。

### Task 2: 页面接入

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`
- Modify: `Views/Motion/Controls/ManualMotionControlPanel.xaml`

- [x] Home构造新增 `ForkManualControl = new ForkManualControlViewModel();`；工位资料加载后调用 `ForkManualControl.LoadMappings(machineRows);`；程序Exit释放手动对象。
- [x] 标题下面插入独立GroupBox：第一行选择与刷新；第二行四个运动按钮；下面反馈和本次结果。绑定 `ForkManualControl.SelectedLine`、`CanOperate`、四个动作Command及刷新Command。
- [x] 保留原设备选择和运动控制，调整外层Grid行号；标题提示使用“选择设备后手动发送指令，动作结果以设备反馈为准。”。
- [x] `dotnet build AutomaticOnlineHostComputer.csproj --no-restore --verbosity minimal`，检查XAML编译与离线布局，测试时不连接现场PLC或数据库。

### Task 3: 验证、文档与提交

**Files:**
- Modify: `Docs/superpowers/specs/2026-10-10-manual-fork-control-design.md`
- Modify: `Docs/02-通信协议手册/货叉及辅助PLC协议_三菱MC.md`

- [x] 记录使用入口、地址来源、反馈未知、命令只发一次与退出释放；将设计状态改为用户已确认并实现。
- [x] 全量测试与既有367条基线比较（已知10条原有失败）；核对新测试与构建结果。
- [x] `git diff --check`；仅暂存本功能上述文件及本计划，复核暂存差异。
- [ ] `git commit -m "feat: 增加货叉手动运动控制"`；验证提交ID及工作区原有配置修改仍保留，不push。


验证结果：主项目构建0错误，新增20项测试通过（18行为+2布局）；全量387项377通过，10项失败与基线完全一致。760/1100像素离线截图已核对。测试宿主回环通信曾被沙箱Socket10013拦截，获准在限制外运行后完成验证。未连接现场PLC。
