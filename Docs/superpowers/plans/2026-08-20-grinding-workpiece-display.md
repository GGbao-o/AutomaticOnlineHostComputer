# Grinding Workpiece Display Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Write each ST709 workpiece's final diameter and length to a configurable Modbus TCP display without allowing display failures to affect grinding business state.

**Architecture:** A focused `GrindingWorkpieceDisplayService` owns one `IDeviceClient`, reconnects when the configured IP changes, writes registers 100 and 101, and returns failures as data. `GrindingFlowEngine` invokes it after D200 supplementation, X11 validation, and the action-time M730 confirmation, then logs the result without using it as a business gate.

**Tech Stack:** .NET 8, C#, WPF/MVVM, existing `ModbusTcpClient`, xUnit, JSON.

**Execution note:** The user selected inline execution and explicitly requested no Git commit, so commit steps are omitted.

---

## File map

- Create `Communication/DeviceServices/GrindingWorkpieceDisplayService.cs` for display-only Modbus communication.
- Create `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingWorkpieceDisplayContractTests.cs` for configuration, communication, insertion-point, and isolation contracts.
- Modify `Infrastructure/Config/MotionConfig.cs`, `Config/motion_settings.json`, `Presentation/ViewModels/Config/ConfigPageViewModel.cs`, and `Views/Config/ConfigPageView.xaml` for the editable IP.
- Modify `Service/FlowEngine/GrindingFlowEngine.cs` to invoke and dispose the display service.
- Modify `Docs/02-通信协议手册/通信矩阵总览.md`, `Docs/03-设备交互流程/整线引擎流程总览_按代码.md`, and `Docs/全线业务逻辑详解.md`.

### Task 1: Configuration and UI contract

**Files:**
- Create: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingWorkpieceDisplayContractTests.cs`
- Modify: `Infrastructure/Config/MotionConfig.cs`
- Modify: `Config/motion_settings.json`
- Modify: `Presentation/ViewModels/Config/ConfigPageViewModel.cs`
- Modify: `Views/Config/ConfigPageView.xaml`

- [x] **Step 1: Write the failing configuration tests**

```csharp
[Fact]
public void Grinding_display_ip_is_exposed_on_configuration_page()
{
    string model = Read("Infrastructure", "Config", "MotionConfig.cs");
    string json = Read("Config", "motion_settings.json");
    string vm = Read("Presentation", "ViewModels", "Config", "ConfigPageViewModel.cs");
    string view = Read("Views", "Config", "ConfigPageView.xaml");
    Assert.Contains("public string WorkpieceDisplayIp { get; set; } = \"192.168.2.111\";", model);
    Assert.Contains("\"workpieceDisplayIp\": \"192.168.2.111\"", json);
    Assert.Contains("public string GrindingWorkpieceDisplayIp", vm);
    Assert.Contains("_cfg.Grinding.WorkpieceDisplayIp", vm);
    Assert.Contains("工件显示屏 IP", view);
    Assert.Contains("GrindingWorkpieceDisplayIp", view);
}

[Fact]
public void Configuration_page_updates_shared_display_ip()
{
    var config = new MotionConfig();
    var vm = new ConfigPageViewModel(config);
    vm.GrindingWorkpieceDisplayIp = "192.168.2.222";
    Assert.Equal("192.168.2.222", config.Grinding.WorkpieceDisplayIp);
}
```

- [x] **Step 2: Run the focused test and confirm it fails**

```powershell
dotnet test AutomaticOnlineHostComputer.Tests\AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~GrindingWorkpieceDisplayContractTests
```

Expected: FAIL because the properties do not exist.

- [x] **Step 3: Add the minimal configuration implementation**

Add to `GrindingSection`:

```csharp
/// <summary>研磨取料工件参数显示屏 Modbus TCP IP。</summary>
public string WorkpieceDisplayIp { get; set; } = "192.168.2.111";
```

Add `"workpieceDisplayIp": "192.168.2.111"` to the JSON grinding object. Add this ViewModel property:

```csharp
public string GrindingWorkpieceDisplayIp
{
    get => _cfg.Grinding.WorkpieceDisplayIp;
    set { _cfg.Grinding.WorkpieceDisplayIp = value; OnPropertyChanged(); }
}
```

Add this row to the existing “研磨 & 动平衡” card:

```xml
<StackPanel Orientation="Horizontal" Margin="0,0,0,4">
    <TextBlock Text="工件显示屏 IP" Style="{StaticResource ParamLabelStyle}" />
    <TextBox Text="{Binding GrindingWorkpieceDisplayIp, UpdateSourceTrigger=PropertyChanged}"
             Style="{StaticResource ParamTextBoxStyle}" Width="130" />
</StackPanel>
```

- [x] **Step 4: Run the focused test and confirm the configuration tests pass**

### Task 2: Display communication behavior

**Files:**
- Create: `Communication/DeviceServices/GrindingWorkpieceDisplayService.cs`
- Modify: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingWorkpieceDisplayContractTests.cs`

- [x] **Step 1: Add failing fake-client behavior tests**

Use a test fake implementing `IDeviceClient`. Require factory arguments `(IP, 502, 1, 3000)`, writes `(100, rounded diameter)` then `(101, rounded length)`, reuse for the same IP, disposal/recreation after IP changes, and a non-throwing failure result when address101 fails:

```csharp
var result = await service.TryWriteAsync("192.168.2.111", 159.6, 1000.4);
Assert.True(result.Success);
Assert.Equal(new[] { (100, 160), (101, 1000) }, fake.Writes);
```

- [x] **Step 2: Run the focused test and confirm it fails because the service is absent**

- [x] **Step 3: Implement the service API**

```csharp
public sealed record GrindingWorkpieceDisplayWriteResult(bool Success, string Stage, Exception? Error);

public sealed class GrindingWorkpieceDisplayService : IAsyncDisposable
{
    public const int Port = 502;
    public const byte UnitId = 1;
    public const int TimeoutMs = 3000;
    public const int DiameterAddress = 100;
    public const int LengthAddress = 101;

    public GrindingWorkpieceDisplayService(
        Func<string, int, byte, int, IDeviceClient>? clientFactory = null);

    public Task<GrindingWorkpieceDisplayWriteResult> TryWriteAsync(
        string ip, double diameter, double length, CancellationToken ct = default);

    public ValueTask DisposeAsync();
}
```

Serialize calls with a private semaphore. For each call: validate nonblank IP; replace/dispose the client when IP changes; create a linked 3000ms cancellation token; connect if needed; write address100 then101 using `(int)Math.Round`; return success. Rethrow only caller cancellation. Convert configuration/device exceptions into a result carrying stage `配置IP`, `连接`, `直径地址100`, or `长度地址101`, and dispose the failed client so the next call reconnects.

- [x] **Step 4: Run the focused test and confirm all service behavior tests pass**

### Task 3: Grinding-engine insertion and isolation

**Files:**
- Modify: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingWorkpieceDisplayContractTests.cs`
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs`

- [x] **Step 1: Add failing source-order and isolation tests**

Extract `ProcessWorkpieceAsync` and assert:

```csharp
Assert.True(Index("grinder.PendingWorkpiece = wp") < Index("hasExistingWorkpiece = await crane.ReadXBitAsync"));
Assert.True(Index("hasExistingWorkpiece = await crane.ReadXBitAsync") < Index("ConfirmGrindingFeedReadyAsync(\"上料动作前\""));
Assert.True(Index("ConfirmGrindingFeedReadyAsync(\"上料动作前\"") < Index("WriteWorkpieceDisplayBestEffortAsync(wp, ct)"));
Assert.True(Index("WriteWorkpieceDisplayBestEffortAsync(wp, ct)") < Index("await crane.SetAbsSpeedAsync"));
```

Extract the helper and assert it contains the display call and log text, but not `_paused`, `OnSafetyAlarm`, `RequeueWorkpiece`, `PendingWorkpiece`, or `State =`.

- [x] **Step 2: Run the focused test and confirm it fails**

- [x] **Step 3: Implement engine ownership and invocation**

Add `private readonly GrindingWorkpieceDisplayService _workpieceDisplay = new();`. Add:

```csharp
private async Task WriteWorkpieceDisplayBestEffortAsync(WorkpieceCache wp, CancellationToken ct)
{
    var result = await _workpieceDisplay.TryWriteAsync(
        _cfg.Grinding.WorkpieceDisplayIp, wp.Diameter, wp.Length, ct);
    if (result.Success)
    {
        Console.WriteLine($"[GrindingEngine] [工件显示屏] 写入成功 IP={_cfg.Grinding.WorkpieceDisplayIp}:502 d={(int)Math.Round(wp.Diameter)}→100 L={(int)Math.Round(wp.Length)}→101");
        return;
    }
    Console.WriteLine($"[GrindingEngine] [工件显示屏] ⚠ 写入失败但继续取料 IP={_cfg.Grinding.WorkpieceDisplayIp}:502 阶段={result.Stage} d={wp.Diameter} L={wp.Length}: {result.Error?.Message}");
}
```

Call it immediately after the second `ConfirmGrindingFeedReadyAsync("上料动作前", ct)` succeeds and before station-coordinate lookup/speed setup. Dispose it from engine `Dispose()` after canceling `_engineCts`.

- [x] **Step 4: Run focused tests and confirm they pass**

### Task 4: Documentation

**Files:**
- Modify: `Docs/02-通信协议手册/通信矩阵总览.md`
- Modify: `Docs/03-设备交互流程/整线引擎流程总览_按代码.md`
- Modify: `Docs/全线业务逻辑详解.md`

- [x] **Step 1: Add the display endpoint to the communication matrix**

Document configurable IP default `192.168.2.111`, port502, station1, address100 diameter, address101 length, and best-effort display-only semantics.

- [x] **Step 2: Update both grinding flow descriptions**

Document that the write happens after D200 supplementation, X11-clear validation, and the action-time M730 confirmation, before speed setup and movement to ST709. State that a display failure only writes `grinding.log` and never pauses, alarms, mutates Pending/cache, or blocks pickup. Preserve the existing pre-magnet requeue rule.

- [x] **Step 3: Search all docs for timing contradictions**

```powershell
rg -n "显示屏|地址100|地址101|ST709|M730|D200" Docs
```

### Task 5: Verification and planning closure

**Files:**
- Modify: `task_plan.md`
- Modify: `findings.md`
- Modify: `progress.md`

- [x] **Step 1: Run the focused tests**

```powershell
dotnet test AutomaticOnlineHostComputer.Tests\AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~GrindingWorkpieceDisplayContractTests
```

- [x] **Step 2: Run the complete suite**

```powershell
dotnet test AutomaticOnlineHostComputer.Tests\AutomaticOnlineHostComputer.Tests.csproj --no-restore
```

- [x] **Step 3: Build the application**

```powershell
dotnet build AutomaticOnlineHostComputer.csproj --no-restore
```

- [x] **Step 4: Check whitespace and worktree scope**

```powershell
git diff --check
git status --short
```

Expected: zero test failures and zero build errors; no task files are staged or committed. Preserve all pre-existing user changes.

- [x] **Step 5: Mark Phase 8 complete and record exact verification results**
