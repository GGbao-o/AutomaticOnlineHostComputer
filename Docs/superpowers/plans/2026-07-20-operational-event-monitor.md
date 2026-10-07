# 生产异常监控结构化升级 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在不改变任何生产分支、状态、缓存、锁、暂停、重试、超时或设备命令的前提下，建立最多5000条、可聚合、可筛选、可复制和可导出的结构化生产异常观察系统。

**Architecture:** 新增纯内存 `OperationalEventReporter → OperationalEventStore` 单向旁路；业务调用点只复制已经掌握的证据并调用 `void Report/Observe`，Reporter内部吞掉全部异常。现有 `AttentionEventCenter` 作为兼容适配器，页面改为左侧事件列表、右侧八组详情，CSV/TXT仅在用户点击时生成。

**Tech Stack:** .NET 8、WPF、C# records、Microsoft.Extensions.DependencyInjection、System.Text.Json、xUnit；不新增数据库、Excel包、设备读取或后台文件持久化。

---

## 实施总边界

1. 引擎文件允许的变化只有：注入Reporter、创建只供监控使用的动作ID/证据局部值、调用 `Report/Observe`、为命令响应未知增加“上报后原样 `throw;`”的窄catch。
2. 不得删除、移动或改写原有 `if/switch/return/throw`、状态赋值、缓存操作、锁获取释放、`Pause/Resume`、重试、超时、设备命令及其参数。
3. `Service/OperationalEvents` 不得引用 `Communication`、设备服务、数据库服务、引擎类、WPF类型或文件系统API；用户触发的文件写入单独放在 `Infrastructure/Export`。
4. 监控配置只在DI启动阶段读取一次；Reporter运行期不读文件。
5. 所有时间阈值测试使用可注入时钟，不真实等待。
6. 每个任务提交前只暂存该任务列出的文件；工作区其他未提交内容属于用户，不得带入提交。

## 文件结构

新增核心文件：

- `Service/OperationalEvents/OperationalEventTypes.cs`：枚举和八组强类型证据。
- `Service/OperationalEvents/OperationalEventModels.cs`：事件、上下文、快照和诊断统计。
- `Service/OperationalEvents/IOperationalEventReporter.cs`：四个无返回值、不得抛异常的入口。
- `Service/OperationalEvents/OperationalEventStore.cs`：5000条容量、10分钟聚合、版本快照。
- `Service/OperationalEvents/OperationalEventReporter.cs`：规范化、瞬时失败阈值、阶段滞留与恢复。
- `Service/OperationalEvents/OperationalEventCatalog.cs`：稳定事件代码、标题、默认等级和操作指引。
- `Service/OperationalEvents/OperationalEventFormatter.cs`：页面、复制和TXT的统一八组文案。
- `Infrastructure/Export/OperationalEventExporter.cs`：CSV/TXT快照导出。
- `Service/OperationalEvents/OperationalEventMonitorOptions.cs`：Reporter依赖的纯不可变设置。
- `Infrastructure/Config/OperationalEventMonitorOptionsLoader.cs`：启动时一次性读取、校验并合并设置。
- `Config/operational_event_monitor_settings.json`：监控阈值，不是业务超时。
- `Service/FlowEngine/OperationalEventContextFactory.cs`：把 `WorkpieceCache` 和调用点局部值复制成纯证据。
- `Presentation/ViewModels/Home/OperationalEventRowViewModel.cs`：列表和详情展示映射。
- `Presentation/ViewModels/Home/OperationalEventNavigationState.cs`：只读导航新增数量。
- `AutomaticOnlineHostComputer.Tests/`：纯内存核心、格式化、导出、ViewModel和静态业务隔离测试。

修改文件：

- `AutomaticOnlineHostComputer.sln`
- `AutomaticOnlineHostComputer.csproj`
- `Service/AttentionEventCenter.cs`
- `Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- `Presentation/ViewModels/Home/HomeViewModel.cs`
- `Presentation/ViewModels/Home/AttentionMonitorViewModel.cs`
- `Views/AttentionMonitor/AttentionMonitorView.xaml`
- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- `Service/FlowEngine/ProductionFlowEngine.cs`
- `Service/FlowEngine/Line1FrontFlowEngine.cs`
- `Service/FlowEngine/Line2FrontFlowEngine.cs`
- `Service/FlowEngine/Line1RearFlowEngine.cs`
- `Service/FlowEngine/Line2RearFlowEngine.cs`
- `Service/FlowEngine/BalancingFlowEngine.cs`
- `Service/FlowEngine/GrindingFlowEngine.cs`
- `Service/FlowEngine/XAbsFineTuneHelper.cs`

---

### Task 1: 建立测试工程和仓库定位助手

**Files:**
- Modify: `Service/OperationalEvents/IOperationalEventReporter.cs`
- Modify: `Service/OperationalEvents/OperationalEventStore.cs`
- Modify: `AutomaticOnlineHostComputer.Tests/OperationalEvents/OperationalEventStoreTests.cs`
- Create: `Service/OperationalEvents/OperationalObservationIdentity.cs`
- Create: `Service/OperationalEvents/OperationalStageHeartbeat.cs`
- Create: `AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj`
- Create: `AutomaticOnlineHostComputer.Tests/Usings.cs`
- Create: `AutomaticOnlineHostComputer.Tests/RepositoryRoot.cs`
- Create: `AutomaticOnlineHostComputer.Tests/SmokeTests.cs`
- Modify: `AutomaticOnlineHostComputer.sln`
- Modify: `AutomaticOnlineHostComputer.csproj`

- [x] **Step 1: 创建测试工程文件**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <PlatformTarget>x86</PlatformTarget>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\AutomaticOnlineHostComputer.csproj" />
  </ItemGroup>
</Project>
```

`Usings.cs`：

```csharp
global using System.IO;
global using Xunit;
```

主项目使用SDK默认递归编译规则，因此必须在 `AutomaticOnlineHostComputer.csproj` 排除测试源码，避免主WPF项目重复编译xUnit文件：

```xml
<ItemGroup>
  <Compile Remove="AutomaticOnlineHostComputer.Tests\**\*.cs" />
  <Page Remove="AutomaticOnlineHostComputer.Tests\**\*.xaml" />
  <None Remove="AutomaticOnlineHostComputer.Tests\**\*" />
  <EmbeddedResource Remove="AutomaticOnlineHostComputer.Tests\**\*.resx" />
  <Content Remove="AutomaticOnlineHostComputer.Tests\**\*" />
  <Resource Remove="AutomaticOnlineHostComputer.Tests\**\*" />
</ItemGroup>
```

- [x] **Step 2: 增加测试仓库根目录助手和冒烟测试**

```csharp
namespace AutomaticOnlineHostComputer.Tests;

internal static class RepositoryRoot
{
    public static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AutomaticOnlineHostComputer.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("未找到AutomaticOnlineHostComputer.sln");
    }
}

public sealed class SmokeTests
{
    [Fact]
    public void Repository_root_contains_main_project()
    {
        string root = RepositoryRoot.Find();
        Assert.True(File.Exists(Path.Combine(root, "AutomaticOnlineHostComputer.csproj")));
    }
}
```

- [x] **Step 3: 把测试工程加入解决方案**

Run: `dotnet sln AutomaticOnlineHostComputer.sln add AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj`

Expected: 输出“已将项目添加到解决方案”。

- [x] **Step 4: 还原、运行测试和构建**

Run: `dotnet restore AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj`

Expected: 还原成功；当前机器缓存中已有计划使用的三个测试包版本。

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore`

Expected: 1个测试通过，0个失败。

Run: `dotnet build AutomaticOnlineHostComputer.sln --no-restore`

Expected: 0个错误。

- [x] **Step 5: 提交测试基座**

```powershell
git add -- AutomaticOnlineHostComputer.Tests AutomaticOnlineHostComputer.sln AutomaticOnlineHostComputer.csproj
git commit -m "test: add operational event test harness"
```

---

### Task 2: 定义强类型事件、证据和监控配置

**Files:**
- Create: `Service/OperationalEvents/OperationalEventTypes.cs`
- Create: `Service/OperationalEvents/OperationalEventModels.cs`
- Create: `Service/OperationalEvents/OperationalEventMonitorOptions.cs`
- Create: `Infrastructure/Config/OperationalEventMonitorOptionsLoader.cs`
- Create: `Config/operational_event_monitor_settings.json`
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/OperationalEventModelTests.cs`
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/OperationalEventMonitorOptionsTests.cs`
- Modify: `AutomaticOnlineHostComputer.csproj`

- [x] **Step 1: 先写模型真实性和配置回退测试**

```csharp
[Fact]
public void Unavailable_position_does_not_use_zero_as_evidence()
{
    var evidence = PositionEvidence.Unavailable("调用点没有成功坐标快照");
    Assert.Equal(EvidenceAvailability.Unavailable, evidence.DisplayX.Availability);
    Assert.False(evidence.DisplayX.HasValue);
    Assert.Equal(EvidenceAvailability.Unavailable, evidence.AbsX.Availability);
    Assert.False(evidence.AbsX.HasValue);
}

[Fact]
public void Invalid_monitor_config_falls_back_without_throwing()
{
    string path = Path.GetTempFileName();
    File.WriteAllText(path, "{ invalid json");
    OperationalEventMonitorOptions options = OperationalEventMonitorOptionsLoader.Load(path);
    Assert.Equal(5000, options.Capacity);
    Assert.Equal(TimeSpan.FromMinutes(10), options.AggregationWindow);
    Assert.Equal(3, options.TransientFailureCountThreshold);
}
```

- [x] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter "OperationalEventModelTests|OperationalEventMonitorOptionsTests"`

Expected: FAIL，缺少 `PositionEvidence` 和 `OperationalEventMonitorOptions`。

- [x] **Step 3: 创建完整枚举和八组证据类型**

`OperationalEventTypes.cs` 必须定义以下稳定类型，nullable值和 `EvidenceAvailability` 共同表达未知，不允许用0冒充读取结果：

```csharp
namespace AutomaticOnlineHostComputer.Service.OperationalEvents;

public enum OperationalEventSeverity { Critical, Error, Warning, Information }
public enum OperationalEventCategory { Safety, FinalFailure, PhysicalUnknown, AutomaticRecovery, Communication, StageStall, FineTune, Motion, Magnet, Sensor, Handshake, Emergency }
public enum EvidenceAvailability { Confirmed, Inferred, Unknown, Unavailable, NotApplicable }
public enum DeviceCommandState { NotSent, SentUnconfirmed, Acknowledged, Failed, Unknown, Unavailable, NotApplicable }
public enum PhysicalConclusionCode { Unknown, CommandNotSent, CommandResultUnknown, WorkpieceConfirmedHeld, WorkpieceConfirmedPlaced, SafePositionConfirmed, ManualConfirmationRequired }
public enum RecoveryStepState { NotAttempted, Succeeded, Failed, Skipped, Unknown }

public sealed record EvidenceValue<T>(EvidenceAvailability Availability, T Value,
    bool HasValue, string Reason)
{
    public static EvidenceValue<T> Confirmed(T value, string reason) =>
        new(EvidenceAvailability.Confirmed, value, true, reason);
    public static EvidenceValue<T> Unknown(string reason) =>
        new(EvidenceAvailability.Unknown, default!, false, reason);
    public static EvidenceValue<T> Unavailable(string reason) =>
        new(EvidenceAvailability.Unavailable, default!, false, reason);
    public static EvidenceValue<T> NotApplicable(string reason) =>
        new(EvidenceAvailability.NotApplicable, default!, false, reason);
}

public sealed record DeviceCommandEvidence(DeviceCommandState State,
    EvidenceAvailability Availability, string Reason);
public sealed record PhysicalConclusionEvidence(PhysicalConclusionCode Code,
    EvidenceAvailability Availability, string Summary, string Basis);

public sealed record WorkpieceEvidence(EvidenceValue<string> PlateNo,
    EvidenceValue<string> Sequence, EvidenceValue<double> Diameter,
    EvidenceValue<double> Length, EvidenceValue<string> Source,
    EvidenceValue<string> Target, EvidenceValue<string> SoftwareOwner,
    EvidenceValue<string> LastConfirmedLocation);

public sealed record PositionEvidence(EvidenceValue<int> DisplayX,
    EvidenceValue<int> DisplayY, EvidenceValue<int> DisplayZ,
    EvidenceValue<int> AbsX, EvidenceValue<int> AbsY,
    EvidenceValue<int> TargetX, EvidenceValue<int> TargetY,
    EvidenceValue<int> TargetAbsX, EvidenceValue<int> TargetAbsY,
    EvidenceValue<int> DeltaX, EvidenceValue<int> DeltaY,
    EvidenceValue<int> Tolerance, EvidenceValue<int> MaximumCorrection,
    EvidenceValue<int> StableSampleCount, EvidenceValue<int> FineTuneAttemptCount,
    EvidenceValue<DateTime> CapturedAtUtc)
{
    public static PositionEvidence Unavailable(string reason) =>
        OperationalEvidenceDefaults.UnavailablePosition(reason);
}

public sealed record MotionAndMagnetEvidence(DeviceCommandEvidence ZDownCommand,
    EvidenceValue<bool> ZMayStillBeLow, DeviceCommandEvidence MagnetOnCommand,
    DeviceCommandEvidence MagnetOffCommand, EvidenceValue<int> X11LastValue,
    EvidenceValue<bool> X11ReadValid, EvidenceValue<int> X11Attempts,
    EvidenceValue<DateTime> X11ReadAtUtc);

public sealed record PhysicalCommitmentEvidence(string Name,
    EvidenceValue<bool> State, string Detail);
public sealed record BusinessStateEvidence(EvidenceAvailability Availability, string Reason,
    EvidenceValue<string> LastSuccessfulCheckpoint,
    EvidenceValue<string> StateBefore, EvidenceValue<string> StateAfter,
    EvidenceValue<string> CacheBefore, EvidenceValue<string> CacheAfter,
    EvidenceValue<string> OwnerBefore, EvidenceValue<string> OwnerAfter,
    EvidenceValue<bool> HoldingWorkpiece, EvidenceValue<bool> Placed,
    EvidenceValue<bool> CacheNotified,
    IReadOnlyList<PhysicalCommitmentEvidence> PhysicalCommitments);

public sealed record LockItemEvidence(string Name, EvidenceValue<bool> HeldAtFailure,
    EvidenceValue<bool> ReleasedAfterward, string ManualConfirmation);
public sealed record LockEvidence(EvidenceAvailability Availability, string Reason,
    IReadOnlyList<LockItemEvidence> Items);
public sealed record RecoveryStepEvidence(string Step, RecoveryStepState State, string Detail);
public sealed record RecoveryEvidence(EvidenceAvailability Availability, string Reason,
    EvidenceValue<bool> Attempted,
    EvidenceValue<bool> Completed, IReadOnlyList<RecoveryStepEvidence> Steps,
    IReadOnlyList<string> PostRecoveryVerification, string ResultingBusinessBehavior);
public sealed record OperatorGuidance(EvidenceAvailability Availability, string Reason,
    IReadOnlyList<string> RequiredActions, IReadOnlyList<string> ForbiddenActions,
    string ContinueCondition);
public sealed record ExceptionEvidence(EvidenceAvailability Availability, string Reason,
    string Type, string Message, string InnerExceptionChain,
    string BusinessContext, string StackTrace);
public sealed record OperationalEvidence(WorkpieceEvidence Workpiece, PositionEvidence Position,
    MotionAndMagnetEvidence MotionAndMagnet, BusinessStateEvidence BusinessState,
    LockEvidence Locks, RecoveryEvidence Recovery, OperatorGuidance Guidance,
    ExceptionEvidence Exception)
{
    public static OperationalEvidence Unavailable(string reason) =>
        OperationalEvidenceDefaults.Unavailable(reason);
}
```

`OperationalEvidenceDefaults.Unavailable` 必须为八组字段的每个值分别创建Unknown/Unavailable/NotApplicable证据，并使用独立的新集合保存PhysicalCommitments、Locks、Recovery步骤、复核项和操作指引。空集合只表示“已确认没有相关项”；未采集必须由集合所属证据组的Availability和Reason明确表达。Reporter接收上下文及Store保存事件时还要再次防御性复制所有嵌套集合，调用方后续修改原List不得改变历史事件，向下转换Snapshot集合也不得可写。

- [x] **Step 4: 创建事件上下文、聚合事件、快照和配置**

`OperationalEventModels.cs` 使用同一组属性名贯穿后续任务：

```csharp
public sealed record OperationalEventContext
{
    public required string EventCode { get; init; }
    public required OperationalEventSeverity Severity { get; init; }
    public required OperationalEventCategory Category { get; init; }
    public required string Scope { get; init; }
    public required string Engine { get; init; }
    public required string DeviceType { get; init; }
    public required string DeviceNo { get; init; }
    public required string Station { get; init; }
    public required string ActionStage { get; init; }
    public required string Title { get; init; }
    public string Source { get; init; } = string.Empty;
    public string ActionId { get; init; } = string.Empty;
    public string CorrelationKey { get; init; } = string.Empty;
    public bool IndependentAction { get; init; }
    public string DetailMessage { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public Exception? CapturedException { get; init; }
    public PhysicalConclusionEvidence PhysicalConclusion { get; init; } =
        new(PhysicalConclusionCode.Unknown, EvidenceAvailability.Unknown, "物理结论未知", "调用点没有确认依据");
    public EvidenceValue<bool> BusinessPaused { get; init; } =
        EvidenceValue<bool>.Unknown("调用点没有暂停状态证据");
    public required OperationalEvidence Evidence { get; init; }
    public DateTime? OccurredAtUtc { get; init; }
}

public sealed record OperationalEvent(string EventId, string FirstActionId,
    string LatestActionId, string EventCode,
    OperationalEventSeverity Severity, OperationalEventCategory Category,
    DateTime FirstOccurredAtUtc, DateTime LastOccurredAtUtc, long OccurrenceCount,
    string Scope, string Engine, string Source, string DeviceType, string DeviceNo, string Station,
    string ActionStage, string Title,
    PhysicalConclusionEvidence FirstPhysicalConclusion,
    PhysicalConclusionEvidence LatestPhysicalConclusion,
    EvidenceValue<bool> FirstBusinessPaused,
    EvidenceValue<bool> LatestBusinessPaused,
    string FirstDetailMessage, string LatestDetailMessage,
    string FirstResult, string LatestResult,
    OperationalEvidence FirstEvidence, OperationalEvidence LatestEvidence);

public sealed record OperationalEventDiagnostics(long TotalReceived, long AggregatedCount,
    long EvictedCount, long ReporterFailureCount, DateTime? LastReporterFailureAtUtc,
    string LastReporterFailureReason);
public sealed record OperationalEventStoreSnapshot(long Version,
    IReadOnlyList<OperationalEvent> Events, OperationalEventDiagnostics Diagnostics);
```

配置默认值必须是：容量5000、聚合10分钟、连续失败3次或30秒、锁60秒、R6101/R6103 10分钟、R6107 90分钟、货叉/天车返回5分钟、中转架全满10分钟、打号机B文件30分钟、其他无业务超时等待10分钟。

`OperationalEventMonitorOptions` 只保存已校验的纯设置；`OperationalEventMonitorOptionsLoader.Load(string? path = null)` 才允许文件/JSON I/O。默认路径为 `AppContext.BaseDirectory/Config/operational_event_monitor_settings.json`。缺文件、空文件、JSON null、无权限、I/O异常、类型错误和单项非法都不得抛出；部分配置与内置字典合并，容量大于5000时压到5000，所有次数/秒数小于等于0时回退对应默认值，StageCode字典使用OrdinalIgnoreCase并防御性复制。

调用点只给 `CapturedException=ex` 和简单字符串；Reporter内部再把异常类型、Message、InnerException链和StackTrace规范化到 `ExceptionEvidence`。引擎不得调用异常链格式化器。`BusinessPaused` 必须是 `EvidenceValue<bool>`：只有调用点确知暂停状态时才Confirmed；旧Attention文本入口没有暂停参数，只能Unknown，禁止按事件Kind推断true或false。

`Config/operational_event_monitor_settings.json`：

```json
{
  "capacity": 5000,
  "aggregationWindowSeconds": 600,
  "transientFailureCountThreshold": 3,
  "transientFailureDurationSeconds": 30,
  "stageThresholdSeconds": {
    "SOFTWARE_LOCK": 60,
    "ZONE_MT": 60,
    "ZONE_TS": 60,
    "POSITION_LOCK": 60,
    "R6101": 600,
    "R6103": 600,
    "R6107": 5400,
    "FORK_RETURN": 300,
    "CRANE_RETURN": 300,
    "TRANSFER_RACK_FULL": 600,
    "MARKER_B_FILE": 1800,
    "DEFAULT_NO_BUSINESS_TIMEOUT": 600
  }
}
```

在主项目 `.csproj` 增加 `None Update="Config\operational_event_monitor_settings.json"`、`CopyToOutputDirectory=PreserveNewest` 和 `CopyToPublishDirectory=PreserveNewest`。

配置测试除无效JSON外，必须覆盖缺文件、空文件、JSON null、部分字典、零/负数、capacity大于5000、未知键和StageCode大小写；模型测试必须证明 `EvidenceValue<bool>.Confirmed(false, reason)` 与Unknown不同、真实坐标0与未取得不同、单个坐标字段可在其他字段已知时独立Unavailable、锁持有false与证据未知不同、空集合与未采集集合不同，并证明修改输入List或可写数组不会改变默认/复制后的证据集合。

- [x] **Step 5: 运行测试并提交**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter "OperationalEventModelTests|OperationalEventMonitorOptionsTests"`

Expected: 全部通过。

```powershell
git add -- Service/OperationalEvents/OperationalEventTypes.cs Service/OperationalEvents/OperationalEventModels.cs Service/OperationalEvents/OperationalEventMonitorOptions.cs Infrastructure/Config/OperationalEventMonitorOptionsLoader.cs Config/operational_event_monitor_settings.json AutomaticOnlineHostComputer.csproj AutomaticOnlineHostComputer.Tests/OperationalEvents
git commit -m "feat: add typed operational event model"
```

---

### Task 3: 实现线程安全Store、Reporter、聚合和阈值状态机

**Files:**
- Create: `Service/OperationalEvents/IOperationalEventReporter.cs`
- Create: `Service/OperationalEvents/OperationalEventStore.cs`
- Create: `Service/OperationalEvents/OperationalEventReporter.cs`
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/OperationalEventStoreTests.cs`
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/OperationalEventReporterTests.cs`

- [x] **Step 1: 写失败测试覆盖聚合、容量、吞错、连续失败和滞留恢复**

测试必须逐项断言：同指纹滚动10分钟内合并且边界 `<=` 合并、`>` 新建；乱序时间不会倒置首次/最近证据；不同工件/严重度/物理结论不合并；首次证据不覆盖；最新证据更新；第5001项淘汰最久未更新项；旧窗口同指纹被淘汰时不误删新节点索引；Store版本和诊断计数精确且单调；并发上报总次数正确；第3次或下一次失败观察距首次满30秒创建通信事件；未达到阈值的单次失败不创建事件；阈值前恢复只清周期、阈值后恢复只创建一次恢复事件；阶段达到阈值只提示一次；同Key收到 `Completed` 后才按是否已上报决定是否创建恢复事件，收到 `Aborted` 绝不伪报恢复；抛异常Store、时钟、ReporterFailure记录或Changed订阅者都不会把异常传给调用方。

另外覆盖聚合关联：未知工件不同ActionId不合并、相同ActionId可合并；未知工件无ActionId但有相同CorrelationKey可合并、不同CorrelationKey不合并；ActionId和CorrelationKey均为空时每次使用唯一nonce禁止合并；已知工件的独立动作不同ActionId不合并。CorrelationKey只作技术聚合，不得写入First/Latest ActionId冒充业务动作。快照测试必须证明顶层和全部嵌套集合均不可写，修改原始List或一个Snapshot不能改变已保存事件和下一Snapshot。

核心断言：

```csharp
reporter.ObserveTransientFailure(Failure("MC65", clock.UtcNow));
reporter.ObserveTransientFailure(Failure("MC65", clock.UtcNow));
Assert.Empty(store.Snapshot().Events);
reporter.ObserveTransientFailure(Failure("MC65", clock.UtcNow));
Assert.Single(store.Snapshot().Events);

clock.Advance(TimeSpan.FromMinutes(11));
reporter.Report(Context(eventCode: "E1", plateNo: "P1"));
reporter.Report(Context(eventCode: "E1", plateNo: "P1"));
Assert.Equal(2, store.Snapshot().Events.Count);
```

- [x] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter "OperationalEventStoreTests|OperationalEventReporterTests"`

Expected: FAIL，接口和实现不存在。

- [x] **Step 3: 定义只写Reporter接口和观察输入**

```csharp
public interface IOperationalEventReporter
{
    void Report(OperationalEventContext context);
    void ObserveTransientFailure(OperationalFailureObservation observation);
    void ObserveRecovery(OperationalRecoveryObservation observation);
    void ObserveStage(OperationalStageObservation observation);
}

public sealed record OperationalFailureObservation(string Key, OperationalEventContext Context);
public sealed record OperationalRecoveryObservation(string Key, OperationalEventContext Context);
public enum OperationalStageObservationState { Active, Completed, Aborted }
public sealed record OperationalStageObservation(string Key, string StageCode, string ActionId,
    OperationalStageObservationState State, TimeSpan? ThresholdOverride,
    OperationalEventContext Context);
public interface IOperationalEventClock { DateTime UtcNow { get; } }
public sealed class SystemOperationalEventClock : IOperationalEventClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public interface IOperationalEventStore
{
    event Action? Changed;
    void Record(OperationalEventContext context, DateTime occurredAtUtc,
        long observationSequence, string fingerprint);
    void RecordReporterFailure(DateTime occurredAtUtc, string reason);
    OperationalEventStoreSnapshot Snapshot();
}
```

四个Reporter方法全部 `try/catch`；catch内用独立嵌套 `try/catch` 包住“读取时钟 + `store.RecordReporterFailure`”整个表达式，任何二次异常也直接吞掉。`Report(null!)`、时钟首次读取失败、异常链规范化失败和极端字段也不得传播。Reporter不得返回bool、事件ID或任何可用于业务判断的值，不得把原始Exception对象保存进Store。

- [x] **Step 4: 实现Store和Reporter**

Store使用一个短锁、`LinkedList<OperationalEvent>` 和 `Dictionary<string, LinkedListNode<OperationalEvent>>`。Reporter在每个观察入口最早处用 `Interlocked.Increment` 分配单调 `observationSequence` 并传给Store。聚合窗口以当前项 `LastOccurredAtUtc` 为基准滚动计算，负时间差不得延长窗口；Store按UTC发生时间、同时间再按observationSequence成组确定First/Latest ActionId、PhysicalConclusion、Detail、Result、BusinessPaused与Evidence，不能按抢锁顺序简单覆盖。聚合时累加次数并把节点移到链尾；达到容量后从链头淘汰。窗口过期的新同指纹节点替换字典映射；淘汰旧节点时仅当字典仍ReferenceEquals该节点才删除键，禁止误删新索引。EventId并发唯一、聚合时稳定、新窗口新建。指纹严格包含：

```csharp
string.Join("\u001f", context.EventCode, context.Category, context.Scope,
    context.Engine, context.Source, context.DeviceType, context.DeviceNo,
    context.Station, context.ActionStage, context.Title,
    WorkpieceIdentity(context.Evidence.Workpiece), context.CorrelationKey,
    NeedsActionCorrelation(context) ? ActionOrCorrelationOrUniqueNonce(context) : string.Empty,
    context.PhysicalConclusion.Code, context.Severity)
```

`WorkpieceIdentity` 只有版号和序号都为HasValue且非空才返回身份。`NeedsActionCorrelation = IndependentAction || 工件身份为空`；需要关联时优先使用真实ActionId，其次使用技术CorrelationKey，两者都为空才为该次上报生成唯一nonce，禁止未知动作误合并。旧Attention适配器后续使用kind+scope+source+规范化message产生稳定CorrelationKey，使同一旧异常可聚合，但First/Latest ActionId仍保持真实空。指纹字段按长度前缀编码或结构化哈希，null统一为空，禁止分隔符碰撞。

Reporter维护两个有界私有字典：瞬时失败周期和阶段周期。它们只在Reporter私有锁内更新，不持有Store锁；构造好发生项后离开Reporter锁再写Store。瞬时失败状态键至少组合 `Key + Scope + Engine + DeviceType + DeviceNo + Station`，独立动作再加ActionId；状态保存首次失败时间、次数、是否已上报和最新上下文。每次失败在锁内更新，满足 `count >= 配置次数 || now-first >= 配置时长` 后开始上报，阈值后的后续失败继续Record更新聚合项。成功时先原子清除周期：未上报只清除，已上报只生成一次恢复；即使恢复事件写Store失败也不能保留旧周期，下一失败从1开始。

阶段周期规则：同Key首次 `Active` 开始，后续Active只检查阈值且到阈值仅创建一次滞留项；同Key `Completed` 才表示正常关闭，阈值前不生成恢复、阈值后生成一次恢复并包含总滞留时长；`Aborted` 关闭跟踪但绝不生成“已恢复”，若已上报则记录中止结果。null override使用配置，正数优先，零/负数回退配置。阶段状态设置独立容量上限和最旧状态淘汰诊断；Task 11必须为正常退出与异常退出分别发送Completed/Aborted观察。

Store在锁内只修改状态、递增版本并复制Changed委托，离锁后逐个调用 `GetInvocationList()`；每个订阅者独立吞错，一个订阅者失败不影响后续订阅者，也不得递归调用 `RecordReporterFailure`。Store写入再次深复制全部嵌套集合。Snapshot锁内只复制稳定事件引用、版本和诊断值，离锁后再对最多5000条事件深冻结和排序，禁止页面快照长时间阻塞业务Report；最终集合真正不可写，不能直接暴露数组。`TotalReceived`每次成功进入Record加1、`AggregatedCount`仅合并加1、`EvictedCount`逐项增加、ReporterFailure独立计数；新增、聚合、淘汰结果和ReporterFailure更新使Version单调递增，每次Record最多发布一次Changed，Snapshot按最近发生时间倒序。

Reporter状态字典并发更新必须按observationSequence防倒退：LastTouchedSequence取max，LatestContext只接受更大sequence（同sequence再按UTC）的观察；StageCycle同时保存最早StartedAtUtc和LatestOccurredAtUtc/LatestSequence，阈值始终按“已观察最早开始→已观察最近时间”计算，即使t=20观察先取锁、t=0后到也不能漏报10秒滞留。增加可控交错测试。状态淘汰诊断与正式事件Record分别try/catch隔离，诊断失败不得阻止已达阈值的正式事件上报。

- [x] **Step 5: 运行并发测试和全套测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter "OperationalEventStoreTests|OperationalEventReporterTests"`

Expected: 全部通过且测试执行不包含真实Delay。

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore`

Expected: 全部通过。

- [x] **Step 6: 提交核心**

```powershell
git add -- Service/OperationalEvents/IOperationalEventReporter.cs Service/OperationalEvents/OperationalEventStore.cs Service/OperationalEvents/OperationalEventReporter.cs AutomaticOnlineHostComputer.Tests/OperationalEvents
git commit -m "feat: add in-memory operational event reporter"
```

---

### Task 4: 建立事件目录、详细文案和CSV/TXT导出

**Files:**
- Modify: `Service/OperationalEvents/OperationalEventTypes.cs`
- Create: `Service/OperationalEvents/OperationalEventCatalog.cs`
- Create: `Service/OperationalEvents/OperationalEventFormatter.cs`
- Create: `Infrastructure/Export/OperationalEventExporter.cs`
- Modify: `AutomaticOnlineHostComputer.Tests/OperationalEvents/OperationalEventModelTests.cs`
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/OperationalEventFormatterTests.cs`
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/OperationalEventExporterTests.cs`

- [x] **Step 1: 写措辞和导出失败测试**

测试必须证明：`NotSent` 显示“未发送”；`SentUnconfirmed` 显示“可能已发送但未取得确认”；`Acknowledged` 只显示“命令成功返回”，不能写成X11已确认；真实0与缺坐标分别显示0和“未取得+原因”；TXT固定事件总览和八组且所有字段均出现；聚合项同时展示首次/最近证据；CSV带UTF-8 BOM且固定列数/顺序，正确转义中文、逗号、双引号、CR、LF和CRLF；所有外部文本去掉开头空格/Tab/CR/LF后若以 `= + - @` 开头则在原文本前加单引号，负数数值列不得误处理。

```csharp
[Theory]
[InlineData("=1+1", "'=1+1")]
[InlineData("+SUM(A1:A2)", "'+SUM(A1:A2)")]
[InlineData("正常文本", "正常文本")]
public void Csv_cells_are_formula_safe(string input, string expected)
{
    Assert.Equal(expected, OperationalEventExporter.MakeCsvCellSafe(input));
}
```

- [x] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter "OperationalEventFormatterTests|OperationalEventExporterTests"`

Expected: FAIL，目录、Formatter和Exporter不存在。

- [x] **Step 3: 实现稳定事件目录**

目录至少包含以下EventCode：

| EventCode | 默认类别 | 标题 |
|---|---|---|
| `LEGACY_SAFETY_ALARM` | Safety | 既有安全异常 |
| `LEGACY_WARNING` | Safety | 既有黄色警告 |
| `LEGACY_EMERGENCY` | Emergency | 应急操作结果 |
| `ENGINE_FINAL_FAILURE` | FinalFailure | 流程最终失败 |
| `CRANE_XY_FINE_TUNE_FAILED` | FineTune | XY绝对编码器微调失败 |
| `CRANE_MAGNET_ON_RESPONSE_UNKNOWN` | PhysicalUnknown | 充磁命令响应未知 |
| `CRANE_MAGNET_OFF_FAILED` | Magnet | 退磁失败 |
| `CRANE_X11_READ_FAILED` | Sensor | X11最终读取失败 |
| `CRANE_X11_NOT_CONFIRMED` | Sensor | X11未确认持件 |
| `PRESSURE_STOP_DETECTED` | Motion | Z下压保护触发 |
| `PRESSURE_STOP_RECOVERED` | AutomaticRecovery | 下压保护自动恢复 |
| `PHYSICAL_HANDOFF_NOT_CLOSED` | PhysicalUnknown | 物理交接未闭环 |
| `DEVICE_TRANSIENT_FAILURE` | Communication | 设备连续通信失败 |
| `DEVICE_CONNECTION_RECOVERED` | AutomaticRecovery | 设备通信恢复 |
| `STAGE_STALLED` | StageStall | 阶段等待超过观察阈值 |
| `STAGE_RECOVERED` | AutomaticRecovery | 阶段恢复推进 |

目录项使用 `OperationalEventDefinition(EventCode, Title, DefaultSeverity, DefaultCategory, RequiredActions, ForbiddenActions, ContinueCondition)`；EventCode唯一，标题和三类指引不得为空，不得含“自行处理/酌情处理”等模糊措辞。每项同时给出稳定的 `RequiredActions` 和 `ForbiddenActions`。物理未知事件必须禁止“仅凭软件false变量重复取料或清工件身份”。未知EventCode使用安全回退，保留现场Title/Severity/Category和全部证据，不抛异常、不输出空详情。

现场事件的Severity、Category和Title优先于目录默认值；调用点具体OperatorGuidance优先，目录指引作为保底，合并去重且顺序稳定；ContinueCondition单独展示，不能混入“必须操作”。

- [x] **Step 4: 实现Formatter和Exporter**

先根据Task 8预审把PositionEvidence一次性稳定下来：单一Tolerance/MaximumCorrection拆为X/Y各自字段；增加最后实际发送Display X/Y目标、XY微调命令证据、失败阶段，以及阶段读取/总读取/反馈重读计数。Unavailable默认值和模型测试同步覆盖真实0、混合可用性与新字段，避免Task 8再次改导出契约。

`OperationalEventFormatter.FormatFullDetail` 先固定输出事件总览，再输出八个标题：线路设备与阶段、工件与流向、坐标与微调、Z磁铁与X11、状态缓存与承诺点、暂停锁与恢复、必须和禁止操作、完整异常链。事件总览必须含EventId/EventCode、First/Latest ActionId（无值写“无动作上下文”）、标题/严重度/类别/来源、首次/最近时间/次数、首次/最近物理结论Code/Availability/Summary/Basis，以及首次/最近DetailMessage、Result、BusinessPaused（Availability/HasValue/Value/Reason）。八组逐字段输出，任何未取得值都必须写“未知/不可用/不适用+原因”，不得留白。自检统计同时展示TransientStateEvictedCount和StageStateEvictedCount。

`OccurrenceCount > 1` 时必须按“首次证据”和“最近证据”分别完整展示八组，或对每个字段显示“首次→最近”；异常链分别保留首次/最近Type、Message、InnerExceptionChain、BusinessContext和StackTrace，不截断、不用模板覆盖。没有异常对象的状态观察显示“不适用：该事件来自状态观察/既有文本”。单次事件可避免机械重复，但不得漏字段。

Exporter公开以下纯格式化API和异步文件API：

```csharp
public static byte[] BuildCsv(IReadOnlyList<OperationalEvent> events);
public static string BuildText(IReadOnlyList<OperationalEvent> events,
    OperationalEventFormatter formatter);
public static Task WriteCsvAsync(string path, IReadOnlyList<OperationalEvent> events,
    CancellationToken cancellationToken = default);
public static Task WriteTextAsync(string path, IReadOnlyList<OperationalEvent> events,
    OperationalEventFormatter formatter, CancellationToken cancellationToken = default);
```

`BuildCsv` 和 `BuildText` 必须是纯内存函数；`Write*Async`入口先对事件列表做不可变快照后才执行文件I/O，且只能由后续页面的用户主动导出命令调用。Exporter不得定时、静态初始化或后台持久化；导出失败不Report生产异常，只交给UI显示导出失败。

CSV一条聚合事件一行，固定有序展开以下列族，禁止把八组详情压成唯一一列：事件身份/首次与最近动作ID/时间/次数/严重度/类别；线路/引擎/来源/设备类型/设备号/工位/阶段；首次与最近工件及流向；显示XYZ/Abs/目标/偏差/容差/采样与尝试次数；Z/充磁/退磁/X11；状态机/缓存/所有权/每个物理承诺点；暂停/锁/恢复步骤/恢复后复核；必须操作/禁止操作/继续条件；首次与最近异常链。每个可缺失证据（含命令与物理结论）统一导出Availability、HasValue、Value、Reason，不能用空单元格混淆Unknown/Unavailable/NotApplicable。动态PhysicalCommitments/Locks/RecoverySteps/PostRecoveryVerification使用稳定JSON列（另有Count），完整保留 `| ; =`、CR/LF、引号和空字符串，禁止用不可逆分隔符拼接或把空值替换成“未提供”。

CSV严格采用RFC4180：固定列数和顺序、CRLF记录分隔、包含逗号/双引号/CR/LF的字段整体加引号且内部双引号加倍；UTF-8 BOM恰好一次；公式安全处理先于RFC转义并应用全部外部文本字段。数值和时间用InvariantCulture，时间为带UTC标识ISO-8601。TXT多事件使用稳定分隔符并含序号/EventId/首次与最近时间/次数；空集合明确输出“无可导出事件”，TXT也使用UTF-8 BOM写文件以兼容现场记事本。

- [x] **Step 5: 测试并提交**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter "OperationalEventFormatterTests|OperationalEventExporterTests"`

Expected: 全部通过。

```powershell
git add -- Service/OperationalEvents/OperationalEventTypes.cs Service/OperationalEvents/OperationalEventCatalog.cs Service/OperationalEvents/OperationalEventFormatter.cs Infrastructure/Export/OperationalEventExporter.cs AutomaticOnlineHostComputer.Tests/OperationalEvents
git commit -m "feat: format and export operational events"
```

---

### Task 5: 兼容现有Attention事件并注册单例依赖

**Files:**
- Modify: `Service/AttentionEventCenter.cs`
- Modify: `Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs:95-121`
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/AttentionEventCenterAdapterTests.cs`

- [x] **Step 1: 写旧事件映射测试**

分别调用 `SafetyAlarm/Warning/Emergency`，断言Store中产生 `LEGACY_SAFETY_ALARM/LEGACY_WARNING/LEGACY_EMERGENCY`，Scope、Source、Message和Result都保留；旧入口没有暂停参数，BusinessPaused必须Unknown，禁止按Kind猜测。相同kind+scope+source+规范化message生成相同CorrelationKey并在10分钟内聚合，不同source/message/category不误合并，First/Latest ActionId保持真实空。Reporter抛错替身不会从 `Record` 传播。增加DI身份断言，确认具体Store与IStore为同一Singleton、Reporter重复解析为同一Singleton。

- [x] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter AttentionEventCenterAdapterTests`

Expected: FAIL，旧中心尚未委托Reporter。

- [x] **Step 3: 把AttentionEventCenter改为兼容适配器**

保留 `AttentionEventKind`、`AttentionEvent` 字段、`Record(...)` void签名、`Snapshot()`返回类型、`Changed`和`Capacity`公共符号；不得为兼容无参构造创建第二套Store/Reporter。Record整个“规范化+Context构造+Report”包在最外层吞错。固定映射：SafetyAlarm/Warning/Emergency分别到三个LEGACY代码；Scope/Source原样保留，Message→DetailMessage，Result null进入新模型时转空字符串；未知引擎/设备/工位/阶段/物理状态显式Unknown/Unavailable，CapturedException保持null，不得由文本伪造。

使用kind+scope+source+规范化message生成稳定技术CorrelationKey，不得伪造业务ActionId；旧Record的暂停证据只能Unknown。过渡期间 `Snapshot` 从Store已有倒序快照过滤三个legacy code后只取旧公共 `Capacity=500` 的最近项，再映射旧 `AttentionEvent`：OccurredAt使用LastOccurredAtUtc.ToLocalTime，Message/Result取最新值，精确反向映射Kind，空Result恢复null，Sequence使用最近时间ticks作兼容排序；不得保留第二份500条列表，新结构化Store/页面仍保留5000项。`Changed` 只转发Store变化，Record不得手工再触发；转发时逐个订阅者独立吞错。

- [x] **Step 4: 注册配置、时钟、Store、Reporter和Formatter**

DI顺序：

```csharp
services.AddSingleton(_ => OperationalEventMonitorOptionsLoader.Load());
services.AddSingleton<IOperationalEventClock, SystemOperationalEventClock>();
services.AddSingleton<OperationalEventStore>();
services.AddSingleton<IOperationalEventStore>(sp => sp.GetRequiredService<OperationalEventStore>());
services.AddSingleton<IOperationalEventReporter, OperationalEventReporter>();
services.AddSingleton<OperationalEventFormatter>();
services.AddSingleton<AttentionEventCenter>();
```

Store和Reporter必须各只有一个进程级实例。

- [x] **Step 5: 测试、构建并提交**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter AttentionEventCenterAdapterTests`

Expected: 全部通过。

Run: `dotnet build AutomaticOnlineHostComputer.sln --no-restore`

Expected: 0个错误，旧异常页面仍可编译。

```powershell
git add -- Service/AttentionEventCenter.cs Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/AttentionEventCenterAdapterTests.cs
git commit -m "refactor: adapt legacy attention events to reporter"
```

---

### Task 6: 向七个引擎注入Reporter但不改变执行路径

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs:25-32,205-217,2386-2455`
- Modify: `Service/FlowEngine/ProductionFlowEngine.cs:35-45,176-184`
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs:46-59,320-336`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs:46-59,324-340`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:25-45,223-236`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:25-45,221-234`
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs:75-100,392-411`
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs:30-48,273-304`
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/ReporterInjectionContractTests.cs`

- [x] **Step 1: 写静态依赖契约测试**

反射断言七个引擎各自唯一公开构造器恰有一个非optional的 `IOperationalEventReporter` 参数、`_exceptionReporter` 为private readonly正确类型；源码断言Home构造器有接口参数和精确赋值。用平衡括号抽取Home中七种 `new X(...)` 实参段，断言每种恰一处且 `_exceptionReporter` 恰出现一次；断言Home/七引擎均不自行 `new OperationalEventReporter/OperationalEventStore`。架构边界只禁止 `using AutomaticOnlineHostComputer.Communication`、完全限定设备命名空间以及 `CraneService|ManipulatorService|McConnectionCache|ManagementQueryService` 类型标识；不得禁普通单词 `Communication`，因为它是合法事件分类。

- [x] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter ReporterInjectionContractTests`

Expected: FAIL，尚未注入。

- [x] **Step 3: 注入同一Reporter实例**

Home构造函数增加 `IOperationalEventReporter exceptionReporter`，赋给只读字段。七个引擎构造函数增加必填参数并只赋字段；所有现有可选锁参数及默认值保持不变，不添加null/no-op默认值、不在构造器new Reporter。参数位置固定：Production/Grinding末尾；Line1Front/Line2Front在 `stationCoords` 后、`transferRackLock` 前；Line1Rear/Line2Rear在 `sf` 后、可选 `fd` 前；Balancing在 `sc` 后、`lockM817` 前。Home传参对应：前端在 `grindingCoords` 后；后端在 `sharedSafety/sharedSafety2` 后；Balancing在 `grindingCoords` 后；Production/Grinding在末尾。此任务不得新增任何Report调用。

- [x] **Step 4: 运行契约测试和构建**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter ReporterInjectionContractTests`

Expected: 全部通过。

Run: `dotnet build AutomaticOnlineHostComputer.sln --no-restore`

Expected: 0个错误。

Task 5/6测试还要用 `BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild=true, ValidateScopes=true })` 验证DI图，并分别证明Reporter重复解析为同一实例、Home只保存注入接口、七个构造点只传该字段。不得尝试通过解析Home后反射七个实例：Production在构造时创建，其余六个在LoadAsync后才创建且会牵涉数据库/设备。

- [x] **Step 5: 检查差异并提交**

Run: `git diff --unified=0 -- Presentation/ViewModels/Home/HomeViewModel.cs Service/FlowEngine/ProductionFlowEngine.cs Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Service/FlowEngine/BalancingFlowEngine.cs Service/FlowEngine/GrindingFlowEngine.cs`

Expected: 只有using、字段、构造参数、字段赋值和Home传参；没有业务方法体差异。

```powershell
git add -- Presentation/ViewModels/Home/HomeViewModel.cs Service/FlowEngine/ProductionFlowEngine.cs Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Service/FlowEngine/BalancingFlowEngine.cs Service/FlowEngine/GrindingFlowEngine.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/ReporterInjectionContractTests.cs
git commit -m "refactor: inject operational event reporter"
```

---

### Task 7: 修复弹窗去重导致的事件漏记并接入集中最终异常

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs:1836-2004`
- Modify: `Service/FlowEngine/ProductionFlowEngine.cs`（13个最终失败出口）
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/LegacyAlarmOrderingContractTests.cs`

- [x] **Step 1: 写顺序契约测试**

静态测试提取 `HandleLineSafetyAlarm` 和 `HandleStandaloneSafetyAlarm`：每个方法恰1次Record、1次MessageBox、1次BeginInvoke；原Pause种类/数量/顺序不变；顺序固定为全部原Pause → popup锁内读取并首次置位flag → 离锁Record → `if (popupAlreadyShown) return` → 原UI/MessageBox。锁体内禁止Record/Report、Pause、Dispatcher、MessageBox；Record前不得再有去重return；四个popup flag的复位位置/次数不变；零位、共享机械手、后天车黄色警告处理器的Record/弹窗数量不变。

Production静态契约必须逐项覆盖：`MoveCraneToStationAsync` 6个最终false（坐标缺失、预约超时、重预约失败、安全检查失败、移动超时、异常）；`MoveManipulatorToStationAsync` 5个最终false（坐标缺失、预约超时、重预约失败、安全检查失败、移动超时）；`EngineLoopAsync` 的取消和普通异常两个catch。每个出口恰一次旁路上报，原return false、Idle赋值、ReleaseStation、catch/finally数量不变；不新增await、设备调用、Pause、MessageBox或Dispatcher。

- [x] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter LegacyAlarmOrderingContractTests`

Expected: FAIL，Record仍位于去重return之后。

- [x] **Step 3: 只移动监控调用，不移动业务语句**

在两个处理器中保持原Pause第一段和原popup锁竞争顺序。锁内不再直接return，而是用局部 `popupAlreadyShown` 保存原flag状态，并只在首次时按原逻辑置位；离锁后调用 `_attentionEvents.Record(...)`，随后 `if (popupAlreadyShown) return;`，再执行原UI/MessageBox。这样仍由原锁赢家决定唯一弹窗内容，所有调用均记录，Reporter不在安全锁内运行。非法line/engine分支、flag、UI状态、MessageBox和复位顺序保持不变；共享机械手、黄色警告和天车全零路径不改。注释明确“所有异常均记录，仅弹窗去重”。

- [x] **Step 4: 为ProductionFlowEngine最终失败增加旁路Report**

覆盖上述13个最终失败出口，而非只处理catch。增加仅复制已有局部值的私有 `TryReport...` helper，把“Context构造+Report”整体包在try/catch；helper为void，不返回业务判断，不读取全局/PLC/设备/数据库，不新增await。Move方法没有ctx：工件、线路、状态机和BusinessPaused必须Unknown/Unavailable并注明“公共移动入口未接收该上下文”，只记录现有设备类型/编号、目标工位/坐标、失败原因或异常，不猜工件/ActionId，可用技术CorrelationKey。

Move方法在catch/false出口上报时，预约释放尚待原finally执行，锁证据必须写“当前可能仍持有；原finally将尝试释放；结果尚未确认”，禁止写已释放，也不得提前ReleaseStation。用不参与业务判断的monitoringStage在原GetService/安全检查/设速/移动前读取/Move/移动后读取前更新，catch报告最后具体动作阶段，不能统称“绝对移动执行”；另用motionMayHaveStarted初始false，只在紧邻原MoveAbsoluteAsync前置true，前置阶段异常必须CommandNotSent，执行/后读异常才CommandResultUnknown。不得增加或移动设备调用。`MoveCraneToStationAsync`中被普通Exception catch接住的OperationCanceledException按Information；`MoveManipulatorToStationAsync`原取消传播方式不变，不新增catch。EngineLoop两个catch先保存失败前stage和真实ctx证据、上报，再保持原Idle复位；取消为Information，其他异常/超时为Error。工件PlateNo/Sequence仅非空Confirmed，Diameter/Length仅>0 Confirmed，否则Unknown。无Exception的false分支仍写明确Detail/Result。

- [x] **Step 5: 测试、业务差异审查和提交**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter LegacyAlarmOrderingContractTests`

Expected: 全部通过。

Run: `git diff --unified=0 -- Presentation/ViewModels/Home/HomeViewModel.cs Service/FlowEngine/ProductionFlowEngine.cs`

Expected: 仅popupAlreadyShown监控局部变量、监控调用/隔离helper及已有值复制；Pause、原flag竞争结果、return、状态赋值、ReleaseStation、catch/finally、MessageBox、Dispatcher和设备调用没有改动。逐项核对13个出口，任何“已释放/已确认”描述都必须有调用点现成证据。

```powershell
git add -- Presentation/ViewModels/Home/HomeViewModel.cs Service/FlowEngine/ProductionFlowEngine.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/LegacyAlarmOrderingContractTests.cs
git commit -m "fix: record alarms independently from popup dedupe"
```

---

### Task 8: 让22个微调调用点产生结构化证据

**Files:**
- Create: `Service/FlowEngine/OperationalEventContextFactory.cs`
- Modify: `Service/FlowEngine/XAbsFineTuneHelper.cs`
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs:1987,2184,2256,2336`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs:1993,2186,2258,2338`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:1352,1434,1743,1967,2000`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:1320,1402,1701,1911,1947`
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs:1374,1458,1660,1727`
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/FineTuneInstrumentationContractTests.cs`

- [x] **Step 1: 写22调用点覆盖测试**

静态测试只枚举5个真实调用文件并精确断言 `VerifyAndFineTuneAsync(` 分布为Line1Front/Line2Front/Line1Rear/Line2Rear/Grinding=`4/4/5/5/4`，合计22；不得把Helper方法声明算成第23处。用平衡括号提取每个真实invocation，断言Reporter、ActionId、工件、来源、目标和物理阶段证据确实位于参数表，不用附近Contains虚绿。行为测试覆盖首次读取失败、稳定失败、超最大修正、第一次/第二次移动后仍超差、反馈未跟随、Reporter抛错不替换原异常、OperationCanceledException不上报且传播不变；并断言原读取/移动/Delay/循环上限/抛错条件数量不变。

- [x] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FineTuneInstrumentationContractTests`

Expected: FAIL，微调助手尚无Reporter上下文。

- [x] **Step 3: 创建纯复制ContextFactory**

Factory可以引用调用点已经持有的 `WorkpieceCache`，但只复制值；不得再读取设备、锁、数据库或缓存。版号/序号为空、直径/长度<=0必须Unknown并注明来源未提供，不能用空串/0包装成Inferred；来源/目标参数使用 `EvidenceValue<string>`，尚未选择的中转架/分流目标为Unknown。异常链展开留给Reporter/Formatter，不在调用点执行。

```csharp
public static WorkpieceEvidence Workpiece(WorkpieceCache wp,
    EvidenceValue<string> source, EvidenceValue<string> target,
    string owner, string lastConfirmedLocation) =>
    // 每字段根据现有值分别构造Confirmed/Inferred/Unknown，不使用0或空串冒充证据

public static string NewActionId(string engineCode) =>
    $"{engineCode}-{Guid.NewGuid():N}";
```

动作ID在完整“微调→Z下降→充磁/退磁→X11/交接”物理周期入口创建，Task 8和Task 9复用同一局部值；只供监控字段使用，不得被任何业务if或状态机读取，不能在Helper调用处临时生成后丢弃。

- [x] **Step 4: 扩展微调助手的监控参数并原样重抛**

助手参数增加Reporter、基础上下文和ActionId。用监控专用累加器保存每次现有 `CraneStatus` 读取已经得到的显示XYZ、AbsX/AbsY及时间，并保存配置Abs目标、计算显示目标、最后实际发送XY目标、X/Y各自偏差/容差/最大修正、当前失败阶段、稳定窗口样本数、阶段/总读取次数、微调次数和反馈重读次数。每个成功阶段更新LastSuccessfulCheckpoint。现有读取、移动、Delay、循环次数和抛错条件一行不改；最外层只增加：

```csharp
catch (Exception ex) when (ex is not OperationCanceledException)
{
    try
    {
        reporter.Report(failureContext with
        {
            EventCode = "CRANE_XY_FINE_TUNE_FAILED",
            ActionId = actionId,
            Evidence = failureContext.Evidence with
            {
                Position = accumulator.ToEvidence(),
            },
            CapturedException = ex,
            DetailMessage = context
        });
    }
    catch { }
    throw;
}
```

整个“证据构造+Report”都在内层保护中，确保永远 `throw;` 原始异常。Helper只覆盖位置/微调/检查点证据，必须保留调用点传入的磁铁、X11、持件、物理结论和Z证据。第一次状态读取失败时，仅显示XYZ、Abs和读数偏差Unavailable；配置目标、X/Y容差/最大修正、失败阶段和已发生尝试次数仍按已知值保留，不能整组丢失或填0。

- [x] **Step 5: 更新22个调用点**

每个调用点只新增动作ID和纯上下文参数。22处按物理阶段分为10个取料前和12个放料前：取料前的MagnetOnCommand=NotSent；放料前使用调用点现成成功返回/X11证据，MagnetOnCommand只能写“Acknowledged=方法成功返回”，持件只由holdingWorkpiece或最后有效X11=1确认。Z下降命令是“本次后续尚未发送”，但ZMayStillBeLow必须由Helper已有最新ZPos、安全Z目标/容差或调用点检查点判断；PressureStop恢复后继续的路径可能仍低位，禁止统一Confirmed(false)。10个取料点中仅8个已有X11=0前置读取，两条打号机取回只能依据退磁成功，不能伪造X11。

来源/目标固定映射：ST713→ST107、ST714→ST502、ST107→中转架未选择、ST502→中转架未选择、1号线rs→bed.Code、2号线rs→bed.Code、1号线bed.Code→ST019/ST010尚未分流、2号线bed.Code→ST020/ST021尚未分流、ST709→grinder.StationCode、grinder.StationCode→ST710；12个放料点按现有rackStation/bed.Code/分流工位/研磨工位记录确切目标。禁止为补字段读取现场。

- [x] **Step 6: 测试、差异审查和提交**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FineTuneInstrumentationContractTests`

Expected: 22个调用点全部覆盖。

Run: `git diff --unified=0 -- Service/FlowEngine/XAbsFineTuneHelper.cs Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Service/FlowEngine/GrindingFlowEngine.cs`

Expected: 原微调命令和Z下降语句顺序不变；只增加监控参数、证据更新、Report和原样重抛。

```powershell
git add -- Service/FlowEngine/OperationalEventContextFactory.cs Service/FlowEngine/XAbsFineTuneHelper.cs Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Service/FlowEngine/GrindingFlowEngine.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/FineTuneInstrumentationContractTests.cs
git commit -m "feat: report all fine tune failures"
```

---

### Task 9: 接入充磁、退磁、X11和物理承诺点

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`（含ST007、Pos3、打号机取回全部物理周期）
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`（含ST007、Pos3、打号机取回全部物理周期）
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:1325-1500,1715-2030`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:1293-1468,1674-1976`
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs:1040-1165,1365-1515`
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs:1243-1570,1592-1810`
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/PhysicalEvidenceInstrumentationTests.cs`

- [ ] **Step 1: 写命令响应未知和承诺点测试**

静态测试精确断言七引擎 `MagnetOnAsync` 和X11读取分布均为Line1Front/Line2Front/Line1Rear/Line2Rear/Balancing/Grinding/Production=`5/5/4/4/4/4/0`，总数26，改造前后调用数和顺序不变。14个逻辑取料周期（两条ST007、两条Pos3、两条打号机取回、两条后天车中转架、两条斜床下料、M2、M3、研磨上下料）全部覆盖；全部26个充磁调用点含首次和重试都能旁路上报 `CRANE_MAGNET_ON_RESPONSE_UNKNOWN`。断言原 `magnetOn/mag=true` 仍只在原await成功后，`holdingWorkpiece=true` 仍只在X11=1分支。断言14个三次X11=0出口、26个最终读取异常、全部36个退磁调用失败、退磁成功但缓存/PLC通知失败分别产生对应结构化事件。

实施前复核补充：12个取料前残留检查中，X11=1必须记录独立的 `CRANE_X11_UNEXPECTED_WORKPIECE`，残留工件身份保持Unknown，不得冒充本次计划工件。通用物理周期tracker必须按14个周期分段，提供X11 Begin/Complete/Fail、磁铁命令开始/返回/异常和Z下降/安全状态；失败读取也计尝试并把本次有效性置false，同时保留最后一次成功值。现有FineTune tracker不得跨Pos3、打号机等物理周期泄漏陈旧X11/Z证据。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter PhysicalEvidenceInstrumentationTests`

Expected: FAIL，关键物理盲区尚未上报。

- [ ] **Step 3: 在首次充磁调用外增加窄catch**

只包住每个原 `await MagnetOnAsync`，统一调用void隔离helper；原来向外抛出的仍用 `throw;`，原来由外层处理的路径不新增return/catch：

```csharp
try
{
    await crane.MagnetOnAsync(ct);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    TryReportMagnetOnUnknown(context, actionId, actionStage, ex);
    throw;
}
```

helper内部将“证据构造+Report”整体try/catch吞掉。MagnetOnCommand必须 `State=Unknown, Availability=Unknown`，原因明确方法可能在手动模式、触发写、反馈轮询或触发复位任一步失败；调用点不能证明已发送，禁止写SentUnconfirmed。“Acknowledged”只表示方法成功返回，不等于已充磁或X11持件。原成功后的 `magnetOn=true`、`mag=true` 位置不动；`holdingWorkpiece=true`仍只在X11=1分支，catch不修改业务变量。退磁异常同理写“退磁结果未知”，不能断言工件仍吸附。

- [ ] **Step 4: 在现有最终失败出口补充X11、退磁和交接证据**

允许增加纯监控X11累加器：每次现有业务读取前次数+1并标当前读取无效，成功后保存最后值/UTC时间/有效；不得参与业务if或增加读取。X11Attempts只计业务层26个读取调用，不包含CraneService.SafeIoAsync内部通信重试。首次即失败记录值Unavailable/有效false/次数1；前两次0第三次失败保留最后成功0、当前有效false、次数3。14个三次0出口只结论“X11未确认持件”，不得推断工件仍在来源位；所有26个读取最终异常记录READ_FAILED。

Z诊断变量区分未开始、调用开始未返回、成功返回、PressureStop恢复后位置未知、已成功回安全高度；不得统一写下降已确认或不可能低位。使用现有 `holdingWorkpiece/placed/cacheNotified`、bed LoadHandshakeState、缓存/PLC通知和finally锁证据，按物理承诺点分别记录“退磁成功返回”“推断已在目标位”“缓存已通知”“PLC已通知”“完整闭环”，不能压成一个bool。magnetOn/mag只代表命令方法返回，不代表持件。

全部36个MagnetOffAsync异常都由隔离helper报告 `CRANE_MAGNET_OFF_FAILED`，至少保证两条前端机械手、两条后天车中转架取料、两条斜床三次X11=0补救catch无遗漏；原来吞掉的补救异常仍吞掉，原来传播的仍原样传播。某字段在当前catch不可见时标Unavailable，不额外读取。窄catch发生时外层finally尚未释放锁/修改缓存，只能写“当前可能仍持有，finally预计尝试释放，结果尚未取得”，禁止提前写已释放；最终失败项再记录最终状态，不为同一出口重复Report增加聚合次数。

- [ ] **Step 5: 测试和业务差异审查**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter PhysicalEvidenceInstrumentationTests`

Expected: 全部通过。

Run: `git diff --unified=0 -- Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Service/FlowEngine/BalancingFlowEngine.cs Service/FlowEngine/GrindingFlowEngine.cs`

Expected: 26个MagnetOn、26个X11和36个MagnetOff原调用数量/顺序不变；所有新增helper不返回业务值，原catch类型、throw/吞错、return、重试次数、暂停、状态赋值、缓存、锁和报警顺序未改；注入抛错Reporter时业务仍得到原异常对象/类型/堆栈。

- [ ] **Step 6: 提交**

```powershell
git add -- Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Service/FlowEngine/BalancingFlowEngine.cs Service/FlowEngine/GrindingFlowEngine.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/PhysicalEvidenceInstrumentationTests.cs
git commit -m "feat: report unknown physical command results"
```

---

### Task 10: 记录50个PressureStop触发和既有自动恢复结果

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`（10个catch）
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`（10个catch）
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`（10个catch）
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`（10个catch）
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs`（6个catch）
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs`（4个catch）
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/PressureStopInstrumentationTests.cs`

- [ ] **Step 1: 写50个catch覆盖契约测试**

用Roslyn/固定清单断言6文件catch分布 `10/10/10/10/6/4=50`，并分类为40个直接传播恢复、8个前端私有包装恢复（内部吞恢复异常）、2个Grinding顶层不恢复人工介入。50个catch都有DETECTED；只有取得真实恢复成功证据的路径才可RECOVERED。不得用裸字符串计数：两个Front私有helper内部额外Recover调用会让简单grep虚绿。44个匿名catch只在不改变catch选择时命名为pEx用于证据；原4个ex和2个pEx保留。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter PressureStopInstrumentationTests`

Expected: FAIL，当前50个catch未全部记录。

- [ ] **Step 3: 在每个catch中包围原恢复调用增加两次Report**

所有“证据构造+Report”通过void隔离helper整体try/catch，监控失败绝不能阻止原恢复。第一次报告实际下压触发、工位、当前动作、Z可能低位、持件状态和原异常；catch时finally尚未执行，Manipulator/ZoneMT/ZoneTS/Grinding锁的ReleasedAfterward为Unknown并注明待finally，禁止写已释放。magnetOn/mag只表示命令方法返回，不能单独Confirmed持件。

40个直接恢复保持原调用原位，只有真实调用成功返回后报告RECOVERED；恢复失败先失败隔离报告 `PRESSURE_STOP_RECOVERY_FAILED`，随后必须 `throw;` 保持原传播。RECOVERED只写“既有下压保护复位序列成功返回且D4523=0”，不等于Z回零/安全高度/流程完全恢复。8个Front包装恢复不能把helper正常返回当成功：在包装器内部围绕真实设备恢复调用记录Succeeded/Failed并复用同一ActionId，保持原吞异常行为；底层失败必须记录FAILED且不得RECOVERED。每次触发固定为DETECTED加RECOVERED/FAILED二选一，不能三条齐报。2个Grinding顶层catch不恢复，只记录DETECTED，严禁新增设备命令；旧动作return和当前动作暂停分支在catch入口不能统一写已暂停。

Rear握手第二类catch发生在Z下降调用/恢复路径之后的Y移动，只能证明“Z下降命令曾尝试发送，此后没有安全升高确认，Z可能仍低位”；第一个PressureStop恢复后原流程也会继续Y移动，禁止写成“Z已成功到位”。4处Front恢复后仍包装抛InvalidOperationException的原行为不变，ResultingBusinessBehavior写“原业务仍终止并进入外层异常处理”；其余继续/重试路径也按原事实描述。Detected与恢复结果使用同一ActionId。

- [ ] **Step 4: 运行测试并复核恢复调用计数**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter PressureStopInstrumentationTests`

Expected: catch仍50且分类40/8/2；两个Grinding不新增恢复；8个包装恢复失败不报Recovered；40个直接恢复失败不报Recovered且传播不变。

Run: `git diff --unified=0 -- Service/FlowEngine | rg --pcre2 '^[-+].*(RecoverFromPressureStopAsync|PressureStopException)'`

Expected: 每个原恢复调用仍存在且调用次数不变；只增加catch变量名、纯监控局部量和隔离Reporter helper。证据构造或Reporter抛错时，原恢复、重试、传播及包装InvalidOperationException行为不变。

- [ ] **Step 5: 提交**

```powershell
git add -- Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Service/FlowEngine/BalancingFlowEngine.cs Service/FlowEngine/GrindingFlowEngine.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/PressureStopInstrumentationTests.cs
git commit -m "feat: record pressure stop recovery events"
```

---

### Task 11: 接入连续通信失败、恢复和阶段滞留观察

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs:700-900,950-1560,2056-2120`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs:700-920,970-1570,2060-2125`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`（主循环、斜床轮询和重连catch）
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`（主循环、斜床轮询和重连catch）
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs:640-830`
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs:650-950,1038-1165`
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/ObservationInstrumentationTests.cs`

- [ ] **Step 1: 写观察入口覆盖测试**

用固定覆盖矩阵断言明确设备I/O入口的失败/有效恢复，而非把六个主循环泛化为通信失败。每个Operation使用同一不可变 `OperationalObservationIdentity`，失败和恢复的Key、Scope、Engine、DeviceType、DeviceNo、Station、CorrelationKey及独立动作字段完全一致。共享fork/boring faulted Task只在创建单次I/O的唯一位置观察一次，多个消费catch不得重复计数。主循环未知异常直接Report最终异常，不走DEVICE_TRANSIENT_FAILURE。

覆盖矩阵至少包括：Front初始化/重连/有效快照（机械手1、货叉、MC63、中转架、双头镗、M800、就绪检查、周期读取）；Rear每斜床初始化/重连/有效poll、整体扫描超时、PollInFlight、FANUC有效Connected快照、Modbus动作重连；Balancing MC63/65、机械手2/3初始化重连/快照/安全X；Grinding MC64/65、四研磨机各自重连/扫描/扫描超时、天车通信。连接成功与帧/快照读取成功使用不同Operation Key。

阶段矩阵至少包括Front的R6101/R6103/R6107、M913/货叉返回、中转架全满、ZoneMT/ZoneTS、打号机B文件；Rear全部斜床握手、ZoneMT/ZoneTS和M817/M818/M720/M821位置锁；Balancing四类资源及握手等待；Grinding六处WaitForGrinderSignal和最终GrindingStuckTimeout。稳定ActionId不得每轮GUID：Rear复用Lease.Version、Grinding actionVersion、Balancing现有action version、Front用纯监控递增版本并跨货叉返回保留。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter ObservationInstrumentationTests`

Expected: FAIL，观察入口尚未接入。

- [ ] **Step 3: 在现有catch/成功路径增加通信观察**

`OperationalObservationIdentity`集中生成Failure/Recovery观察，禁止在两个调用点分别拼字符串。成功必须是有效结果已经提交：Rear仅pollVersion仍有效且ok=true；Grinding仅TryCommitGrinderSignals=true；FANUC仅本轮LatestSnapshot.Connected且有效；降频跳过、Svc=null、过期结果、仅StartPollWorker均不恢复。没有历史失败时成功只清空无事件。观察“上下文构造+Reporter”整体置于吞错helper，不改变重连周期、Task.Run、Interlocked、Delay、原异常或下一业务语句。Reporter达到3次/30秒前不创建事件。

- [ ] **Step 4: 在现有等待循环增加阶段观察**

阶段Key包含线路、引擎、稳定ActionId和StageCode。重复轮询每轮Active；条件真正满足才Completed；异常/取消/Stop/应急丢弃/强制复位/提前返回在集中阶段转换或finally中Aborted。普通读取失败但仍停留原等待阶段不Aborted。跨轮状态机必须集中管理进入/退出，局部等待用finally闭合，测试结束后跟踪状态回0。

Zone/位置锁等一次性无限 `WaitAsync` 不会自行重复Active，因此使用只读 `OperationalStageHeartbeat`：它只在旁路定时调用ObserveStage，不替换、不取消、不超时原业务等待；业务仍只await原AcquireAsync。成功后Complete，非成功出口Abort，内部定时/Context/Reporter全部吞错。不得修改EmergencyActionLease的锁语义或给业务增加超时。打号机B文件保留1小时业务deadline和原轮询间隔，监控阈值30分钟；其他已有业务超时以原超时/2且最低60秒观察，无业务超时用配置默认。

- [ ] **Step 5: 测试、构建和差异审查**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter ObservationInstrumentationTests`

Expected: 全部通过。

Run: `dotnet build AutomaticOnlineHostComputer.sln --no-restore`

Expected: 0个错误。

Run: `git diff --unified=0 -- Service/FlowEngine`

Expected: 原等待条件、业务Delay/超时、重连、共享I/O创建次数和状态迁移没有删除或改序；只增加Identity、旁路观察、稳定监控ActionId和一次性等待Heartbeat。测试证明过期poll/commit=false/FANUC未Connected/降频跳过不恢复，同一faulted共享Task只计一次，Active在Completed/Aborted/Stop/应急所有出口闭合，观察构造或Reporter抛错不影响原catch/finally和锁释放。

- [ ] **Step 6: 提交**

```powershell
git add -- Service/OperationalEvents/OperationalObservationIdentity.cs Service/OperationalEvents/OperationalStageHeartbeat.cs Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Service/FlowEngine/BalancingFlowEngine.cs Service/FlowEngine/GrindingFlowEngine.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/ObservationInstrumentationTests.cs
git commit -m "feat: observe communication failures and stalls"
```

---

### Task 12: 重构ViewModel为主从、全筛选和无漏刷刷新

**Files:**
- Create: `Presentation/ViewModels/Home/OperationalEventRowViewModel.cs`
- Create: `Presentation/Services/OperationalEventUiServices.cs`
- Modify: `Presentation/ViewModels/Home/AttentionMonitorViewModel.cs`
- Modify: `Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `AutomaticOnlineHostComputer.Tests/Presentation/AttentionMonitorViewModelTests.cs`

- [ ] **Step 1: 写ViewModel失败测试**

覆盖：快照倒序；选中事件详情；时间、严重度、类别、线路、引擎、设备类型/号、工位、暂停四态、自动恢复四态、重复和全文筛选；所有条件AND；刷新三种尾事件竞态最终AppliedVersion==CurrentVersion；事件更新/淘汰/筛除时选择规则；5000条聚合只重建变化Row；KPI和全部诊断；Dispose生命周期；空集合；复制；导出当前筛选/全部的同版本结构化快照范围、取消/失败/并发导出。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter AttentionMonitorViewModelTests`

Expected: FAIL，旧VM只支持类型和Scope。

- [ ] **Step 3: 实现RowViewModel和筛选**

`IOperationalEventStore` 增加O(1)线程安全 `CurrentVersion` 和 `GetDiagnosticsSnapshot()`，只复制标量，不深复制5000条，供刷新追赶和Task13红点；补Store测试。`OperationalEventRowViewModel` 保留只读 `SourceEvent`，暴露列表字段、八组详情、惰性缓存的 `FullDetailText` 和一次构建的 `SearchText`。以EventId索引复用未变化Row（LastOccurred+Count判定），一次批量替换/DeferRefresh，禁止Changed业务线程做Snapshot/Formatter/ObservableCollection操作。任何getter不得读取设备。

筛选语义固定：时间按LastOccurredAtUtc且双边包含，本地边界只转换一次；严重度/类别nullable enum；线路/引擎/设备类型/设备号/工位OrdinalIgnoreCase精确；暂停=全部/最近明确true/最近明确false/Unknown，禁止Unknown当false；自动恢复=全部/有/无/Unknown，结合Category和Recovery Attempted/Completed证据；重复=Count>1；全文含EventId、First/Latest ActionId、EventCode、全部定位、工件、First/Latest Detail/Result、八组和异常链；空白词不筛选，多条件AND。动态选项刷新保留用户当前选择，即使得到0项。

`OperationalEventUiServices.cs` 定义 `IUiDispatcher`、`IClipboardService`、`IOperationalEventSaveDialog`、`IOperationalEventExportService`。Dialog返回含规范路径和Csv/Txt格式的 `OperationalEventSaveTarget?`，设置Filter/AddExtension/默认扩展名/时间戳，取消返回null；ExportService适配现有静态Exporter以便测试。生产实现包装WPF Dispatcher/Clipboard/SaveFileDialog；测试使用立即/手动排队Dispatcher和内存替身，不启动真实窗口。服务在DI注册为Singleton。

- [ ] **Step 4: 修复版本追赶刷新**

刷新使用独立 `_refreshGate` 和请求代次。Changed锁内只置标志，锁外Post Dispatcher，兼容立即重入；回调不碰UI。Dispatcher批次完成后在gate内比较Store.CurrentVersion与appliedVersion：落后则保持queued并锁外再投递，追上才清false；覆盖Snapshot后/UI应用前、应用后/清标志前、清后立即变化三种竞态。开始/结束检查disposed；Dispatcher或刷新失败只更新页面错误，不Report生产异常。VM实现IDisposable取消Changed，排队旧回调不得改集合；公开 `RequestRefresh()` 复用同一队列，Task13 Loaded不得另写同步路径。

- [ ] **Step 5: 增加复制和导出命令**

复制只调用 `Clipboard.SetText(SelectedEvent.FullDetailText)`；无选择禁用，失败只更新状态。导出Dialog仅UI线程调用；选定后从当前已应用Row复制 `SourceEvent[]`：当前筛选按可见排序，全部按当前已应用版本，禁止重新读Store或从文本反构造。再后台调用ExportService；`IsExporting`禁止并发，取消不算失败，空路径/对话框/格式化/写入异常只更新OperationStatusText，不Report、不自动保存、不写数据库。

- [ ] **Step 6: 测试并提交**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter AttentionMonitorViewModelTests`

Expected: 全部通过。

```powershell
git add -- Presentation/ViewModels/Home/OperationalEventRowViewModel.cs Presentation/Services/OperationalEventUiServices.cs Presentation/ViewModels/Home/AttentionMonitorViewModel.cs Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs AutomaticOnlineHostComputer.Tests/Presentation/AttentionMonitorViewModelTests.cs
git commit -m "feat: add operational event master detail view model"
```

---

### Task 13: 实现左列表右详情页面和导航新增数量

**Files:**
- Create: `Presentation/ViewModels/Home/OperationalEventNavigationState.cs`
- Modify: `Views/AttentionMonitor/AttentionMonitorView.xaml`
- Modify: `Views/AttentionMonitor/AttentionMonitorView.xaml.cs`
- Modify: `MainWindow.xaml:63-66`
- Modify: `MainWindow.xaml.cs:13-37`
- Modify: `Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `AutomaticOnlineHostComputer.Tests/Presentation/OperationalEventNavigationStateTests.cs`

- [ ] **Step 1: 写导航数量状态测试**

新增或更新聚合事件时按成功接收事件数递增；ReporterFailure/状态淘汰不亮；构造前已有事件计入未看；0隐藏、1-99原数、>=100显示99+；可见期间新增保持0，离开后重新计数；MarkViewed不改Store。覆盖后台多Changed合并、旧排队刷新→MarkViewed不复活、UI Dispatcher线程、异常吞掉、Enter/Leave幂等和Dispose。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter OperationalEventNavigationStateTests`

Expected: FAIL，导航状态不存在。

- [ ] **Step 3: 实现只读导航状态并接入MainWindow**

状态使用Task12的O(1) `GetDiagnosticsSnapshot()`，禁止在Store同步Changed链调用完整Snapshot。回调只O(1)排队，所有PropertyChanged通过IUiDispatcher的单次排队+尾追赶执行。锁内单调维护 `_lastObservedTotalReceived`、`_lastViewedTotalReceived`、`_isViewing`，旧快照不得降低基线；UnseenCount用long，并暴露HasUnseenEvents/UnseenDisplayText。

生命周期：`EnterViewing()`设置可见并清零，之后TotalReceived增长同步推进viewed使红点保持0；`LeaveViewing()`只退出可见态，不清事件，离开后增长才累计；MarkViewed仅更新基线。状态注册Singleton并实现IDisposable。MainWindow构造在首次 `GetOrCreatePage("main")` 前解析状态；异常MenuItem单独DataContext，不能覆盖整个窗口。导航必须先成功取得页面并赋给ContentHost，再由Loaded/EnterViewing清零；禁止导航失败却先MarkViewed。其他菜单行为不变。

- [ ] **Step 4: 替换页面为左列表右详情**

XAML顶部分为运行周期说明、5类KPI、自检统计、筛选和复制/导出按钮；主体两列Grid：左DataGrid绑定SelectedEvent TwoWay、IsReadOnly、禁增删并开启行列虚拟化，显示最近时间、等级、标题、线路设备工位、阶段、工件和次数；右ScrollViewer固定八组，每个Expander单内容容器且文本换行。所有按钮只能复制和导出；不得出现刷新、已阅、确认、删除、恢复、暂停、清报警或设备控制Command。绑定名由Task12测试/反射契约固定，避免运行时静默空白。

- [ ] **Step 5: 增加页面进入通知**

页面 `Loaded` 只调用导航状态 `EnterViewing()` 和VM `RequestRefresh()`；`Unloaded` 调 `LeaveViewing()`，不清VM集合、筛选、选择或Store。代码隐藏不得解析设备服务或HomeViewModel，不启动Timer。

- [ ] **Step 6: 测试、构建并提交**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter OperationalEventNavigationStateTests`

Expected: 全部通过。

Run: `dotnet build AutomaticOnlineHostComputer.sln --no-restore`

Expected: 0个错误，XAML编译成功。

```powershell
git add -- Presentation/ViewModels/Home/OperationalEventNavigationState.cs Views/AttentionMonitor/AttentionMonitorView.xaml Views/AttentionMonitor/AttentionMonitorView.xaml.cs MainWindow.xaml MainWindow.xaml.cs Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs AutomaticOnlineHostComputer.Tests/Presentation/OperationalEventNavigationStateTests.cs
git commit -m "feat: add read-only operational event monitor UI"
```

---

### Task 14: 完整业务隔离复核、回归测试和文档收尾

**Files:**
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/BusinessIsolationArchitectureTests.cs`
- Modify: `Docs/superpowers/specs/2026-07-20-operational-event-monitor-design.md`（仅追加实施结果和验证命令）

- [ ] **Step 1: 增加架构边界测试**

测试按语法/锚定正则扫描 `Service/OperationalEvents`，禁止using/全限定Communication、DeviceServices、FlowEngine、MySql、System.Windows和具体设备类型，不误报合法枚举/注释。核心目录零文件I/O，Reporter零File/Directory/Stream/SaveFileDialog；基础设施I/O白名单只有OptionsLoader启动读JSON和Exporter用户导出。XAML解析交互元素的Command/Click/Checked/Binding Path，白名单Copy/Export/Select/Filter，禁止Pause/Resume/Recover/Reset/Acknowledge/Confirm/Retry/DeviceWrite；不扫描帮助文本中文。断言DataGrid只读且禁增删。

业务旁路架构测试同时断言：Reporter四方法均void；七引擎/Home只持有注入接口；Reporter只能赋值/传构造/调用四个void方法，不得出现在if/return/三元/业务变量；Report/Observe不await；新增窄catch保持原取消语义和 `throw;`，全功能路径无 `throw ex;`。

- [ ] **Step 2: 运行全部测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --no-incremental`

Expected: 全部测试通过，0个失败。

- [ ] **Step 3: 运行完整构建**

Run: `dotnet build AutomaticOnlineHostComputer.sln --no-restore --no-incremental`

Expected: 0个错误；记录最终警告总数并确认监控新增文件没有警告。当前没有同命令/同SDK/同工作树的可信Task0警告基线，文档不得宣称“警告零增量”。不执行dotnet clean，避免触碰已跟踪且含用户改动的bin/obj。

- [ ] **Step 4: 做业务逻辑差异审查**

逐个打开七个引擎和Home差异，建立检查表并签字确认：

- 原分支条件和return不变；
- 原状态、缓存和工件所有权赋值顺序不变；
- 原catch/finally和锁释放不变；
- 原设备命令及参数不变；
- 原暂停、恢复、重试和业务超时不变；
- 新动作ID/证据只流向Reporter，不被业务条件读取；
- 新窄catch全部 `throw;`，没有 `throw ex;`；
- 没有为补详情增加PLC/CNC/设备读取。

Run: 基于功能基线 `2873776` 和明确功能路径执行 `git diff --check 2873776..HEAD -- <功能路径>`，并单独执行 `git diff --check HEAD -- <功能路径>` 检查未提交功能差异；列出 `git log --reverse --oneline 2873776..HEAD` 和name-status，逐提交/逐hunk审查七引擎与Home。禁止裸全仓diff、restore/reset/clean或全量stage；前置脏工作区无法数学证明全部差异归属，设计文档必须如实记录该证明边界。

Run: `rg -n "throw ex;|GetRequiredService|App\.Services|Communication|DeviceServices" Service/OperationalEvents Service/FlowEngine/OperationalEventContextFactory.cs`

Expected: 无 `throw ex;`、全局ServiceProvider和设备依赖；Exporter允许的系统文件API不在该检索式中。

- [ ] **Step 5: 做离线人工验收**

用fake clock、内存Store/Reporter和测试UI服务直接构造微调失败、充磁未知、X11失败、PressureStop触发/恢复、通信阈值/恢复、阶段滞留/恢复、同暂停周期两条异常；自动断言代码、首次/最近/次数、八组、动作ID、筛选、详情、红点、复制、CSV/TXT，无真实Delay且不启动生产App（启动链可能连接设备）。视觉布局、系统剪贴板和真实SaveFileDialog列为人工/待现场确认，不能用单测冒充已验收。

- [ ] **Step 6: 更新设计文档实施记录并提交**

文档只追加实际完成的文件清单、测试数量、构建结果、警告口径、前置脏工作区限制、仍未覆盖调用点和未执行人工项；不得提前写“全部完成”。Task14提交严格只有架构测试和设计文档，提交前确认cached name正好两文件并cached diff-check，禁止git add -A。

```powershell
git add -- AutomaticOnlineHostComputer.Tests/OperationalEvents/BusinessIsolationArchitectureTests.cs Docs/superpowers/specs/2026-07-20-operational-event-monitor-design.md
git commit -m "test: verify operational event business isolation"
```

---

## 最终验收矩阵

| 需求 | 对应任务 |
|---|---|
| 强类型八组证据 | Task 2 |
| 5000条、首次/最近/次数聚合 | Task 3 |
| Reporter吞错且不返回业务结果 | Task 3 |
| 稳定详细文案 | Task 4 |
| CSV/TXT且无数据库/Excel依赖 | Task 4、12 |
| 兼容现有安全、警告、应急 | Task 5、7 |
| 同暂停周期后续异常仍记录 | Task 7 |
| 22个微调调用点 | Task 8 |
| 充磁响应未知、X11、退磁、物理闭环 | Task 9 |
| 50个PressureStop及自动恢复 | Task 10 |
| 连续通信失败阈值和恢复 | Task 11 |
| 阶段滞留和恢复 | Task 11 |
| 左列表右详情、完整筛选 | Task 12、13 |
| 导航红点/数量，无新增弹窗 | Task 13 |
| 不改变业务逻辑 | Task 6-11逐任务差异门槛、Task 14总审查 |

## 实施停止条件

出现以下任一情况立即停止当前任务，不继续扩大改动：

1. 为获得详情必须新增一次设备、PLC、CNC或数据库读取；
2. 必须改变原catch分支、状态赋值、缓存、锁或恢复顺序才能上报；
3. Reporter/页面结果被任何业务if、return或状态迁移读取；
4. 新增测试需要真实设备或真实等待；
5. 工作区出现无法确认归属的重叠修改。

此时只记录字段为Unavailable或把调用点列入未覆盖清单，不擅自修改业务处理。
