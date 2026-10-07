# 1/2号线前端在途应急 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增不进入正常自动流程的1/2号线前端在途诊断、继续当前板、两级丢弃复位和普通双头镗清零保护。

**Architecture:** 设备服务只提供原始读写能力，两个前端引擎各自拥有恢复安全门槛和内存状态修改权，HomeViewModel只按线体转发，WPF对话框负责人工确认和二级清理交互。正常主循环不调用任何新增恢复方法。

**Tech Stack:** C# 12、.NET 8 WPF、SemaphoreSlim、三菱MC、Modbus TCP。

---

### Task 1: 设备侧诊断和应急清零原语

**Files:**
- Modify: `Communication/DeviceServices/ForkService.cs`
- Modify: `Communication/DeviceServices/BoringModbusService.cs`

- [ ] **Step 1: 为货叉增加只清动作输出方法**

在 `ForkService` 增加公开方法，复用现有读改写语义，只清M911～M914并保留M900～M910：

```csharp
public async Task ClearMotionCommandsAsync(CancellationToken ct = default)
{
    var result = await _client.ReadAsync(Addr.ReadStartAddr, Addr.ReadWordCount, ct);
    int current = result.IntValues.Length > 0 ? result.IntValues[0] : 0;
    int commandMask = 0;
    for (int m = Addr.M_GoStandby; m <= Addr.M_StandbyToPos3; m++)
        commandMask |= 1 << (m - Addr.ReadStartAddr);
    int next = current & ~commandMask;
    await _client.WriteAsync(Addr.ReadStartAddr, next, ct);
}
```

- [ ] **Step 2: 为双头镗增加R6101～R6108原始快照**

新增 `BoringCycleRawSnapshot`，字段为 `R6101` 到 `R6108` 八个整数；新增 `ReadCycleRawAsync`。现有 `ReadAllSignalsAsync` 改为从原始快照构造布尔快照，保持调用方行为不变。

```csharp
public sealed record BoringCycleRawSnapshot(
    int R6101, int R6102, int R6103, int R6104,
    int R6105, int R6106, int R6107, int R6108);
```

- [ ] **Step 3: 集中双头镗设备清零顺序**

增加 `ClearEmergencyOutputsAsync`，严格按R6108→R6104→R6102写0；普通清零窗口和前端应急均调用该方法，避免两套顺序漂移。

- [ ] **Step 4: 编译检查设备层**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore -p:IntermediateOutputPath=$env:TEMP\AOH_front_obj\ -p:OutputPath=$env:TEMP\AOH_front_bin\`

Expected: 0 errors；允许保留项目原有warnings。

### Task 2: 1号线前端应急接口

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`

- [ ] **Step 1: 增加独立应急互斥和只读在途查询**

增加 `_frontEmergencyLock = new(1, 1)`。新增 `HasFrontInTransit` 与 `GetFrontInTransitBlockReason()`；判定覆盖 `_currentWp`、非Idle阶段、握手和三个防重复标志，不包含 `_craneQueue`。

- [ ] **Step 2: 增加实时诊断方法**

新增 `GetFrontEmergencyInfoAsync(CancellationToken)`，展示内存阶段、门闩、锁、天车队列，并分别实时读取货叉、双头镗和机械手。单个设备读取失败写入诊断文本，但不修改状态。

- [ ] **Step 3: 增加继续当前板方法**

新增 `EmergencyContinuePlateOnForkAsync`。入口设置 `_paused=true`，以0等待方式取得应急互斥，验证阶段和锁；实时要求M900=1、M901=1、M902=0，机械手Z=0且Y为1号线安全Y；随后写M801，成功后仅设置 `_manipulatorClearedFork=true`。失败保持全部业务状态不变。

- [ ] **Step 4: 增加两级丢弃方法**

新增 `EmergencyDiscardCurrentAsync(bool skipDeviceClear, CancellationToken)`。设备模式先清M911～M914和R6108/R6104/R6102，全部成功后才在 `_wpLock` 下清 `_currentWp` 并复位阶段门闩；设备失败返回 `设备侧清零失败:`。软件模式跳过设备写入但保留同样安全门槛。

- [ ] **Step 5: 确认正常状态机无调用**

Run: `rg -n "EmergencyContinuePlateOnForkAsync|EmergencyDiscardCurrentAsync" Service/FlowEngine/Line1FrontFlowEngine.cs`

Expected: 只出现方法定义，不出现在 `EngineLoopAsync` 或正常动作方法中。

### Task 3: 2号线对称实现

**Files:**
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`

- [ ] **Step 1: 复制相同安全接口但使用2号线安全Y**

实现与Task 2相同的属性和方法，机械手校验使用：

```csharp
int safeY = _cfg.SkewBed.GetManipulator1SafeYForLine(2);
```

- [ ] **Step 2: 对比两线应急方法结构**

Run: `rg -n "HasFrontInTransit|GetFrontEmergencyInfoAsync|EmergencyContinuePlateOnForkAsync|EmergencyDiscardCurrentAsync" Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs`

Expected: 两个文件均有完整四类接口，差异仅线号、日志标签和安全Y。

### Task 4: HomeViewModel转发与普通清零保护

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`
- Modify: `Views/Home/HomeView.xaml.cs`
- Modify: `Views/Home/Dialogs/ClearMachineStatusDialog.xaml.cs`

- [ ] **Step 1: 增加按线体转发接口**

增加 `GetFrontEmergencyInfoAsync`、`EmergencyContinueFrontPlateAsync`、`EmergencyDiscardFrontCurrentAsync` 和 `GetBoringClearBlockReason`。应急调用完成后同步 `IsLine1Running`/`IsLine2Running` 为false，因为方法保持暂停。

- [ ] **Step 2: 向普通清零窗口注入只读拦截委托**

构造函数改为：

```csharp
public ClearMachineStatusDialog(
    Dictionary<string, string> stationIps,
    Func<int, string?> getBoringClearBlockReason)
```

`HomeView.xaml.cs` 传入 `_viewModel.GetBoringClearBlockReason`。

- [ ] **Step 3: 双头镗按钮执行前拦截**

把两个表达式事件改为调用统一 `ClearBoringAsync`。若阻断原因非空，弹窗说明软件阶段不会恢复并引导至前端应急；否则仍展示“只清设备寄存器”的警告，再调用集中清零方法。

### Task 5: 应急中心UI

**Files:**
- Modify: `Views/Home/Dialogs/EmergencyCenterDialog.xaml`
- Modify: `Views/Home/Dialogs/EmergencyCenterDialog.xaml.cs`

- [ ] **Step 1: 新增前端页签**

页签包含线体选择、异步刷新、只读诊断框、“板在货叉，确认安全后继续”和“丢弃当前工件并回Idle”两个按钮。

- [ ] **Step 2: 实现异步刷新与防重复点击**

增加 `_frontActionInProgress`，所有前端按钮用 `Interlocked.CompareExchange` 防止并发，执行期间禁用按钮，结束后刷新诊断。

- [ ] **Step 3: 实现继续确认弹窗**

弹窗明确要求板稳定在货叉、机械手不持件、Z=0、本线安全Y、现场无人；确认后调用继续接口，接口仍进行实时硬校验。

- [ ] **Step 4: 实现两级丢弃弹窗**

第一次调用 `skipDeviceClear:false`；返回 `设备侧清零失败:` 时复用现有二次确认逻辑调用 `skipDeviceClear:true`。两次结果都明确保持线体暂停，不自动恢复。

### Task 6: 文档与验证

**Files:**
- Modify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`
- Modify: `task_plan.md`
- Modify: `progress.md`

- [ ] **Step 1: 补充书面化恢复说明**

写明每个变量、适用情景、前置检查、设备清零失败、二次仅清软件、catch/finally语义、普通清零禁用条件和操作步骤。

- [ ] **Step 2: 静态检查正常业务未被接入**

Run: `git diff --check`

Expected: 无空白错误。

Run: `git diff -- Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs`

Expected: 新代码位于公开应急区；正常状态机的地址、坐标、超时和既有阶段赋值没有变化。

- [ ] **Step 3: 完整编译**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore -p:IntermediateOutputPath=$env:TEMP\AOH_front_obj\ -p:OutputPath=$env:TEMP\AOH_front_bin\`

Expected: 0 errors；记录warnings数量及是否均为既有警告。

- [ ] **Step 4: 更新持久计划**

将 `task_plan.md` 阶段18～21标记完成，在 `progress.md` 记录修改文件、编译结果和无法在无PLC环境执行的现场验证项。
