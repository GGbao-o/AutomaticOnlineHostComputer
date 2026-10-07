# Front Dispatch Task Reordering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让尚未入线的总上料架任务通过暂停移出FIFO、再次启动进入队尾，并在主页面实时显示真实取板顺序。

**Architecture:** 新建`FrontDispatchQueueCoordinator`作为全局FIFO和`Dispatching`占用的唯一并发所有者；`TaskRowViewModel`使用显式派发状态控制按钮和顺序显示；`HomeViewModel`负责把队列快照版本化地应用到WPF UI，并以线路缓存写入成功作为`EnteredLine`边界。

**Tech Stack:** C#、.NET 8 WPF、xUnit、`Queue<T>`、`lock`、MVVM属性通知。

---

### Task 1: 建立FIFO协调器及红灯测试

**Files:**
- Create: `Presentation/ViewModels/Home/FrontDispatchQueueCoordinator.cs`
- Create: `AutomaticOnlineHostComputer.Tests/Presentation/FrontDispatchQueueCoordinatorTests.cs`

- [x] **Step 1: 写暂停重排和派发竞态测试**

测试使用以下公开给测试程序集的内部接口：

```csharp
internal enum FrontDispatchPauseOutcome { Removed, NotQueued, Dispatching }

internal sealed class FrontDispatchQueueCoordinator
{
    internal sealed class Item
    {
        public Item(TaskRowViewModel row, WorkpieceCache workpiece);
        public TaskRowViewModel Row { get; }
        public WorkpieceCache Workpiece { get; }
    }

    internal readonly record struct OrderEntry(TaskRowViewModel Row, int Position);
    internal readonly record struct Snapshot(long Version, IReadOnlyList<OrderEntry> Entries);

    public int Count { get; }
    public WorkpieceCache[] GetQueuedWorkpieces();
    public bool TryPeek(out Item? item);
    public bool TryEnqueue(TaskRowViewModel row, WorkpieceCache workpiece, out int position, out Snapshot snapshot);
    public FrontDispatchPauseOutcome TryPause(TaskRowViewModel row, out int oldPosition, out Snapshot snapshot);
    public bool TryBeginDispatch(Item expected, out Item? item, out Snapshot snapshot);
    public Snapshot CompleteDispatch(Item item);
    public Snapshot RequeueHead(Item item);
}
```

核心测试：

```csharp
[Fact]
public void Pause_then_restart_moves_B_to_tail()
{
    var queue = new FrontDispatchQueueCoordinator();
    var a = Row("A"); var b = Row("B"); var c = Row("C");
    queue.TryEnqueue(a, Wp("A"), out _, out _);
    queue.TryEnqueue(b, Wp("B"), out _, out _);
    queue.TryEnqueue(c, Wp("C"), out _, out _);

    Assert.Equal(FrontDispatchPauseOutcome.Removed, queue.TryPause(b, out var oldPosition, out _));
    Assert.Equal(2, oldPosition);
    Assert.True(queue.TryEnqueue(b, Wp("B"), out var newPosition, out var snapshot));
    Assert.Equal(3, newPosition);
    Assert.Equal(new[] { "A", "C", "B" }, snapshot.Entries.Select(x => x.Row.PlateNo));
}

[Fact]
public void Dispatching_item_cannot_be_paused()
{
    var queue = new FrontDispatchQueueCoordinator();
    var a = Row("A");
    queue.TryEnqueue(a, Wp("A"), out _, out _);
    queue.TryPeek(out var expected);
    Assert.True(queue.TryBeginDispatch(expected!, out var dispatching, out _));
    Assert.Equal(FrontDispatchPauseOutcome.Dispatching, queue.TryPause(a, out _, out _));
    Assert.Same(dispatching, expected);
}

private static TaskRowViewModel Row(string plateNo) => new() { PlateNo = plateNo };
private static WorkpieceCache Wp(string plateNo) => new() { PlateNo = plateNo };
```

- [x] **Step 2: 运行目标测试确认红灯**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~FrontDispatchQueueCoordinatorTests --no-restore`

Expected: FAIL，因为协调器类型尚不存在。

- [x] **Step 3: 实现最小线程安全协调器**

所有队列和当前`Dispatching`项只在私有`_gate`下访问。每次队列变更递增`_version`并返回1起始顺序快照；`TryPause`先检查当前`Dispatching`项，再从任意队列位置删除；`RequeueHead`必须复用原`Item`。

- [x] **Step 4: 运行协调器测试确认绿灯**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~FrontDispatchQueueCoordinatorTests --no-restore`

Expected: PASS，0 failed。

### Task 2: 任务行显式派发状态

**Files:**
- Modify: `Presentation/ViewModels/Home/TaskRowViewModel.cs`
- Create: `AutomaticOnlineHostComputer.Tests/Presentation/TaskRowDispatchStateTests.cs`

- [x] **Step 1: 写状态与按钮映射红灯测试**

```csharp
[Theory]
[InlineData(TaskDispatchState.NotQueued, "启动", true, "—")]
[InlineData(TaskDispatchState.Queued, "暂停", true, "2")]
[InlineData(TaskDispatchState.Dispatching, "派发中", false, "—")]
[InlineData(TaskDispatchState.EnteredLine, "已入线", false, "—")]
public void Dispatch_state_controls_action_and_order(TaskDispatchState state, string action, bool enabled, string order)
{
    var row = new TaskRowViewModel();
    switch (state)
    {
        case TaskDispatchState.NotQueued: row.MarkPaused(); break;
        case TaskDispatchState.Queued: row.MarkQueued(2); break;
        case TaskDispatchState.Dispatching: row.MarkDispatching(); break;
        case TaskDispatchState.EnteredLine: row.MarkEnteredLine(); break;
    }
    Assert.Equal(action, row.ActionText);
    Assert.Equal(enabled, row.CanToggleRun);
    Assert.Equal(order, row.QueueOrderText);
}
```

- [x] **Step 2: 增加`TaskDispatchState`及受控状态方法**

```csharp
public enum TaskDispatchState { NotQueued, Queued, Dispatching, EnteredLine }

public string ActionText => DispatchState switch
{
    TaskDispatchState.NotQueued => "启动",
    TaskDispatchState.Queued => "暂停",
    TaskDispatchState.Dispatching => "派发中",
    TaskDispatchState.EnteredLine => "已入线",
    _ => "启动"
};

public bool CanToggleRun => DispatchState is TaskDispatchState.NotQueued or TaskDispatchState.Queued;
public string QueueOrderText => QueuePosition?.ToString() ?? "—";
```

`RequestStart`和`ToggleRun`只发出`OnStartRequested`/`OnPauseRequested`，不提前改变状态。增加`MarkQueued(int? position = null)`、`MarkPaused()`、`MarkDispatching()`、`MarkEnteredLine()`和`SetQueuePosition(int? position)`，每次统一通知`ActionText`、`CanToggleRun`、`IsRunning`、`QueueOrderText`并刷新命令可用性。

- [x] **Step 3: 运行任务行测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~TaskRowDispatchStateTests --no-restore`

Expected: PASS，0 failed。

### Task 3: 接入HomeViewModel派发流程

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs:400-430`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs:3217-3505`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs:4036-4122`
- Test: `AutomaticOnlineHostComputer.Tests/Presentation/FrontDispatchHomeIntegrationContractTests.cs`

- [x] **Step 1: 写Home集成源码合同红灯测试**

测试断言：`HomeViewModel`使用`FrontDispatchQueueCoordinator`；绑定`OnPauseRequested`；暂停调用`TryPause`；派发使用`TryBeginDispatch`；成功后`CompleteDispatch + MarkEnteredLine`；失败使用`RequeueHead + MarkQueued`；旧的“队头未启动/已暂停保持FIFO等待”分支被删除。

- [x] **Step 2: 替换HomeViewModel裸队列字段**

```csharp
private readonly FrontDispatchQueueCoordinator _frontDispatchQueue = new();
private long _appliedFrontDispatchOrderVersion;
public int FrontDispatchCachedCount => _frontDispatchQueue.Count;
```

增加`ApplyFrontDispatchOrderSnapshot`：锁内快照已经由协调器生成；该方法回WPF UI线程，只应用版本不小于`_appliedFrontDispatchOrderVersion`的快照，并为所有`TaskRows`设置数字或“—”。

- [x] **Step 3: 接入启动、暂停和清除**

`AddTask`同时绑定启动与暂停回调。总上料架启动成功后`MarkQueued`；暂停只有`TryPause=Removed`时`MarkPaused`，`Dispatching`时保持/切到`MarkDispatching`并记录拒绝日志。清除`Queued`任务继续移出FIFO并刷新顺序；`Dispatching/EnteredLine`只隐藏页面。

- [x] **Step 4: 接入派发提交边界**

调度器在写线路缓存前用`TryBeginDispatch`原子取得队头，UI转`Dispatching`。`EnqueueWorkpiece`成功返回后调用`CompleteDispatch`、设置`AssignedLine`并`MarkEnteredLine`；失败则`RequeueHead`同一Item、`AssignedLine=0`、`MarkQueued`。删除依赖`row.IsRunning=false`阻塞队头的旧分支。

- [x] **Step 5: 运行协调器、任务行和Home集成测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter "FullyQualifiedName~FrontDispatchQueueCoordinatorTests|FullyQualifiedName~TaskRowDispatchStateTests|FullyQualifiedName~FrontDispatchHomeIntegrationContractTests" --no-restore`

Expected: PASS，0 failed。

### Task 4: 主页面顺序列与文档

**Files:**
- Modify: `Views/Home/HomeView.xaml:344-356`
- Modify: `Docs/全线业务逻辑详解.md`
- Test: `AutomaticOnlineHostComputer.Tests/Presentation/FrontDispatchHomeIntegrationContractTests.cs`

- [x] **Step 1: 在序号后增加顺序列并绑定按钮可用性**

```xml
<DataGridTextColumn Width="45" Binding="{Binding QueueOrderText}" Header="顺序" />
```

交互按钮增加`IsEnabled="{Binding CanToggleRun}"`。

- [x] **Step 2: 更新业务文档**

写明总上料架FIFO的暂停移除、再启动入队尾、派发中/已入线禁用、失败回队头和顺序列语义。

- [x] **Step 3: 运行全部本需求测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter "FullyQualifiedName~FrontDispatch" --no-restore`

Expected: PASS，0 failed。

### Task 5: 最终验证

**Files:**
- Verify all files above.

- [x] **Step 1: 主项目构建**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore -clp:ErrorsOnly`

Expected: Build succeeded，0 errors。

- [x] **Step 2: 完整测试程序集**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore -clp:ErrorsOnly`

Expected: 记录通过/失败总数；若旧字符串合同仍失败，使用HEAD基线逐项证明与本需求无关，不修改现场配置迎合测试。

- [x] **Step 3: 差异和需求清单检查**

检查FIFO状态、按钮、顺序、日志、异常回队和中转架边界均有实现与测试；只报告本需求文件差异，保留其他用户改动。

> 当前工作区包含大量用户未提交文件和构建产物，本计划不执行Git提交。
