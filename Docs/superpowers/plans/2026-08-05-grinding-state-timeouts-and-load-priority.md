# 研磨状态超时与上料优先 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将研磨引擎的上料中、等待下料、下料中超时分别配置为默认 10、20、10 分钟，并在同一引擎中落实上料优先的安全派发顺序。

**Architecture:** `MotionConfig.Grinding` 保存三个分钟配置；配置页直接编辑同一共享对象。`GrindingFlowEngine` 依据当前状态选择并校验对应时长，主循环调用独立的上料、下料派发方法，以“上料启动成功”作为允许下料的唯一前提。

**Tech Stack:** C#、WPF/XAML、JSON 配置、xUnit。

---

### Task 1: 写入状态超时与优先级的失败契约测试

**Files:**
- Create: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingStateTimeoutAndPriorityContractTests.cs`
- Test: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingStateTimeoutAndPriorityContractTests.cs`

- [ ] **Step 1: 写失败测试**

断言配置模型存在 `LoadingTimeoutMinutes`、`WaitingForUnloadTimeoutMinutes`、`UnloadingTimeoutMinutes`，默认值为 10、20、10；引擎按三种 `GrinderState` 选择超时；主循环的 `TryDispatchLoadAsync` 失败后才调用 `TryDispatchUnloadAsync`；配置页面绑定三项分钟输入。

- [ ] **Step 2: 运行测试并确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingStateTimeoutAndPriorityContractTests --no-restore`

Expected: FAIL，因为当前生产代码仍使用 `GrindingStuckTimeoutMs`，且主循环仍先派下料。

### Task 2: 建立三项配置与页面编辑入口

**Files:**
- Modify: `Infrastructure/Config/MotionConfig.cs:209-229`
- Modify: `Config/motion_settings.json:368-378`
- Modify: `Presentation/ViewModels/Config/ConfigPageViewModel.cs:106-110`
- Modify: `Views/Config/ConfigPageView.xaml:400-417`

- [ ] **Step 1: 用分钟属性替换旧统一超时**

在 `GrindingSection` 定义：

```csharp
public int LoadingTimeoutMinutes { get; set; } = 10;
public int WaitingForUnloadTimeoutMinutes { get; set; } = 20;
public int UnloadingTimeoutMinutes { get; set; } = 10;
```

移除不再被引擎使用的 `GrindingStuckTimeoutMs`，并在 JSON 的 `grinding` 段写入 10、20、10。

- [ ] **Step 2: 暴露配置页绑定属性**

每项 ViewModel 属性直接读写 `_cfg.Grinding` 并触发 `OnPropertyChanged()`：

```csharp
public int GrindingLoadingTimeoutMinutes
{
    get => _cfg.Grinding.LoadingTimeoutMinutes;
    set { _cfg.Grinding.LoadingTimeoutMinutes = value; OnPropertyChanged(); }
}
```

为等待下料和下料中建立对应属性。

- [ ] **Step 3: 在配置页新增分钟卡片**

在“研磨 & 动平衡”卡片下新增“研磨状态超时（分钟）”卡片，三行文本框分别绑定 `GrindingLoadingTimeoutMinutes`、`GrindingWaitingForUnloadTimeoutMinutes`、`GrindingUnloadingTimeoutMinutes`，并显示“超时会暂停引擎且保留现场状态”的说明。

### Task 3: 落实状态选择和上料优先派发

**Files:**
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs:704-1011`

- [ ] **Step 1: 状态选择超时**

以私有方法将分钟数按状态映射为 `TimeSpan`；值小于 1 时使用该状态默认值：

```csharp
private TimeSpan GetStateTimeout(GrinderState state) => state switch
{
    GrinderState.Loading => ToSafeTimeout(_cfg.Grinding.LoadingTimeoutMinutes, 10),
    GrinderState.WaitingForUnload => ToSafeTimeout(_cfg.Grinding.WaitingForUnloadTimeoutMinutes, 20),
    GrinderState.Unloading => ToSafeTimeout(_cfg.Grinding.UnloadingTimeoutMinutes, 10),
    _ => TimeSpan.Zero
};
```

循环只对非零时长的三个状态比较 `DateTime.UtcNow - StateChangedAt`。

- [ ] **Step 2: 提取上料派发并先尝试**

将现有上料代码提取为 `TryDispatchLoadAsync(CancellationToken)`，仅在已取得天车锁、通过派发门闩、完成派发前位置检查并启动 `ProcessWorkpieceAsync` 时返回 `true`。无就绪机、M730 无板、FIFO 空、锁忙或位置校验失败均返回 `false`，且不得遗留天车锁。

- [ ] **Step 3: 提取下料派发作为回退**

将现有 `WaitingForUnload` 遍历提取为 `TryDispatchUnloadAsync(CancellationToken)`，保留 Pending 工件、M720、安全位置、动作版本、应急二次确认和锁释放语义。只有上料方法返回 `false` 时才调用它。

- [ ] **Step 4: 更新注释和日志**

将主循环注释、告警文本和状态注释改为“按状态配置的观察时间”与“上料优先；上料不可派发才下料”，不改变任何异常暂停、人工结案或 PLC 写入逻辑。

### Task 4: 回归验证与提交

**Files:**
- Modify: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingStateTimeoutAndPriorityContractTests.cs`
- Modify: `Infrastructure/Config/MotionConfig.cs`
- Modify: `Config/motion_settings.json`
- Modify: `Presentation/ViewModels/Config/ConfigPageViewModel.cs`
- Modify: `Views/Config/ConfigPageView.xaml`
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs`

- [ ] **Step 1: 运行聚焦测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingStateTimeoutAndPriorityContractTests --no-restore`

Expected: PASS，所有超时映射、页面绑定及上料优先断言通过。

- [ ] **Step 2: 构建与全量测试**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: 0 个错误。

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-build --no-restore`

Expected: 0 个失败。

- [ ] **Step 3: 检查和提交**

Run: `git diff --check -- Infrastructure/Config/MotionConfig.cs Config/motion_settings.json Presentation/ViewModels/Config/ConfigPageViewModel.cs Views/Config/ConfigPageView.xaml Service/FlowEngine/GrindingFlowEngine.cs AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingStateTimeoutAndPriorityContractTests.cs`

Expected: 无空白错误。

Run: `git add -- Infrastructure/Config/MotionConfig.cs Config/motion_settings.json Presentation/ViewModels/Config/ConfigPageViewModel.cs Views/Config/ConfigPageView.xaml Service/FlowEngine/GrindingFlowEngine.cs AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingStateTimeoutAndPriorityContractTests.cs && git commit -m "feat: configure grinding state timeouts"`

Expected: 只提交本功能文件。
