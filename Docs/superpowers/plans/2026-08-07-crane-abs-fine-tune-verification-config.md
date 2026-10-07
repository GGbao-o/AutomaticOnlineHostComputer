# 天车绝对编码器微调验证节奏配置 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将天车绝对编码器微调的等待与稳定读取规则改为所有天车共享、可在配置页面即时调整并保存的运行时配置。

**Architecture:** `MotionConfig` 在根级持有一个共享 `AbsFineTuneVerification` 段，使用用户确认的 1200/3/200/1/1200 默认值。`XAbsFineTuneHelper` 在每次调用开始时读取并校验这组值，将其传入稳定读取和微调后沉降计算；X/Y 标定、目标坐标和 ST714 的 `-1` 哨兵兼容逻辑完全不改。配置页仅绑定该共享对象，沿用已有“写入共享对象即生效、保存写回 JSON”的模式。

**Tech Stack:** .NET 8、C#、WPF/XAML、System.Text.Json、xUnit。

---

### Task 1: 为共享验证节奏建立契约测试

**Files:**
- Create: `AutomaticOnlineHostComputer.Tests/FlowEngine/AbsFineTuneVerificationConfigContractTests.cs`
- Modify: `AutomaticOnlineHostComputer.Tests/OperationalEvents/FineTuneInstrumentationContractTests.cs:129-139, 232-477, 592-623`

- [ ] **Step 1: 写入失败的配置模型/页面契约测试**

```csharp
[Fact]
public void Shared_fine_tune_verification_defaults_and_json_are_exposed()
{
    string model = Read("Infrastructure", "Config", "MotionConfig.cs");
    string json = Read("Config", "motion_settings.json");

    Assert.Contains("public AbsFineTuneVerificationSection AbsFineTuneVerification { get; set; } = new();", model);
    Assert.Contains("public int BeforeReadSettleDelayMs { get; set; } = 1200;", model);
    Assert.Contains("public int StableSampleCount { get; set; } = 3;", model);
    Assert.Contains("\"absFineTuneVerification\"", json);
    Assert.Contains("\"afterMoveMinSettleDelayMs\": 1200", json);
}

[Fact]
public void Configuration_page_binds_all_shared_verification_inputs()
{
    string vm = Read("Presentation", "ViewModels", "Config", "ConfigPageViewModel.cs");
    string view = Read("Views", "Config", "ConfigPageView.xaml");

    Assert.Contains("public int AbsFineTuneBeforeReadSettleDelayMs", vm);
    Assert.Contains("public int AbsFineTuneStableSampleCount", vm);
    Assert.Contains("AbsFineTuneAfterMoveMinSettleDelayMs", view);
}
```

- [ ] **Step 2: 执行新测试，确认它在未实现前失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter "FullyQualifiedName~AbsFineTuneVerificationConfigContractTests"`

Expected: FAIL；新配置类型、JSON 段和页面属性尚不存在。

- [ ] **Step 3: 写入失败的配置驱动时序测试**

在 `FineTuneInstrumentationContractTests` 中加入测试，给 `ActiveConfig()` 设置：

```csharp
config.AbsFineTuneVerification.BeforeReadSettleDelayMs = 1300;
config.AbsFineTuneVerification.StableSampleCount = 2;
config.AbsFineTuneVerification.StableSampleIntervalMs = 150;
config.AbsFineTuneVerification.StableRangeMm = 1;
config.AbsFineTuneVerification.AfterMoveMinSettleDelayMs = 1700;
```

以两次相同的微调前状态和两次相同的微调后状态驱动 `FakeFineTuneExecution`，断言调用顺序包含：

```csharp
"delay:Phase:1300",
"read", "delay:StableRead:150", "read",
"move:1:110:-1:-1:5:30000",
"delay:Phase:1700",
"read", "delay:StableRead:150", "read"
```

并将既有 `AddStableReadCalls` 改为接受 `sampleCount` 和 `intervalMs`，使默认路径的断言使用 `config.AbsFineTuneVerification` 的 3 次/200ms，而不是硬编码 5 次/200ms。

- [ ] **Step 4: 执行定向时序测试，确认它在未实现前失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter "FullyQualifiedName~FineTuneInstrumentationContractTests"`

Expected: FAIL；帮助类仍固定使用 500ms、5 次、200ms 和 600ms。

### Task 2: 实现配置模型、默认 JSON 与配置页面

**Files:**
- Modify: `Infrastructure/Config/MotionConfig.cs:42-45, 152-204`
- Modify: `Config/motion_settings.json`（`xAbsFineTune` 前增加根级配置段）
- Modify: `Presentation/ViewModels/Config/ConfigPageViewModel.cs:117-120, 183-188`
- Modify: `Views/Config/ConfigPageView.xaml:259-282`

- [ ] **Step 1: 在 `MotionConfig` 添加共享节奏对象与受限读取方法**

在 X/Y 微调段后添加：

```csharp
/// <summary>所有天车共用的绝对编码器微调验证节奏。</summary>
public AbsFineTuneVerificationSection AbsFineTuneVerification { get; set; } = new();

public sealed class AbsFineTuneVerificationSection
{
    public int BeforeReadSettleDelayMs { get; set; } = 1200;
    public int StableSampleCount { get; set; } = 3;
    public int StableSampleIntervalMs { get; set; } = 200;
    public int StableRangeMm { get; set; } = 1;
    public int AfterMoveMinSettleDelayMs { get; set; } = 1200;

    public FineTuneVerificationValues GetValidated()
    {
        int before = BeforeReadSettleDelayMs is >= 100 and <= 10_000 ? BeforeReadSettleDelayMs : 1200;
        int count = StableSampleCount is >= 2 and <= 20 ? StableSampleCount : 3;
        int interval = StableSampleIntervalMs is >= 50 and <= 2_000 ? StableSampleIntervalMs : 200;
        int range = StableRangeMm is >= 0 and <= 20 ? StableRangeMm : 1;
        int after = AfterMoveMinSettleDelayMs is >= 100 and <= 2_000 ? AfterMoveMinSettleDelayMs : 1200;
        return new(before, count, interval, range, after);
    }
}

public readonly record struct FineTuneVerificationValues(
    int BeforeReadSettleDelayMs, int StableSampleCount, int StableSampleIntervalMs,
    int StableRangeMm, int AfterMoveMinSettleDelayMs);
```

范围确保非法手工 JSON 不会让稳定窗口为空、采样间隔为零或微调后最小沉降超过现有 2000ms 上限。

- [ ] **Step 2: 在部署 JSON 写入用户确认的默认值**

```json
"absFineTuneVerification": {
  "beforeReadSettleDelayMs": 1200,
  "stableSampleCount": 3,
  "stableSampleIntervalMs": 200,
  "stableRangeMm": 1,
  "afterMoveMinSettleDelayMs": 1200
},
```

- [ ] **Step 3: 让 ViewModel 暴露五个即时生效属性，并刷新通知**

```csharp
public int AbsFineTuneBeforeReadSettleDelayMs
{
    get => _cfg.AbsFineTuneVerification.BeforeReadSettleDelayMs;
    set { _cfg.AbsFineTuneVerification.BeforeReadSettleDelayMs = value; OnPropertyChanged(); }
}
```

其余四个属性以同一模式绑定 `StableSampleCount`、`StableSampleIntervalMs`、`StableRangeMm` 和 `AfterMoveMinSettleDelayMs`；在 `RefreshTabs()` 对五个属性逐一执行 `OnPropertyChanged`。

- [ ] **Step 4: 在现有绝对编码器区域添加五项数值输入框**

在 X/Y 启用开关和轴标定参数之间添加标题“微调读取验证（所有天车共用）”，绑定：

```xml
<TextBox Text="{Binding AbsFineTuneBeforeReadSettleDelayMs, UpdateSourceTrigger=PropertyChanged}" />
<TextBox Text="{Binding AbsFineTuneStableSampleCount, UpdateSourceTrigger=PropertyChanged}" />
<TextBox Text="{Binding AbsFineTuneStableSampleIntervalMs, UpdateSourceTrigger=PropertyChanged}" />
<TextBox Text="{Binding AbsFineTuneStableRangeMm, UpdateSourceTrigger=PropertyChanged}" />
<TextBox Text="{Binding AbsFineTuneAfterMoveMinSettleDelayMs, UpdateSourceTrigger=PropertyChanged}" />
```

显示单位 ms、次和 mm，并写明“修改即时影响后续微调；点击保存写入 JSON”。

- [ ] **Step 5: 执行配置契约测试，确认通过**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter "FullyQualifiedName~AbsFineTuneVerificationConfigContractTests"`

Expected: PASS。

### Task 3: 让微调帮助类使用共享配置，而不改变其它安全策略

**Files:**
- Modify: `Service/FlowEngine/XAbsFineTuneHelper.cs:18-26, 416-477, 600-731`
- Modify: `AutomaticOnlineHostComputer.Tests/OperationalEvents/FineTuneInstrumentationContractTests.cs:129-139, 232-477, 592-623`

- [ ] **Step 1: 在每次微调操作开始时获取一次经校验的不可变快照**

在启用轴确认后、第一次延时前执行：

```csharp
FineTuneVerificationValues verification = config.AbsFineTuneVerification.GetValidated();
```

同一次微调全程使用该快照，避免页面运行时编辑导致“微调前”和“微调后”使用两组不同参数。

- [ ] **Step 2: 将五个固定参数替换为快照参数**

```csharp
await DelayWithLogAsync(..., verification.BeforeReadSettleDelayMs, ct);
status = await ReadStableStatusAsync(..., verification, ct);
int settleDelayMs = ComputeAfterMoveSettleDelayMs(maxDelta, verification.AfterMoveMinSettleDelayMs);
```

`ReadStableStatusAsync` 新增 `FineTuneVerificationValues verification` 参数并使用：

```csharp
var window = new List<CraneStatus>(verification.StableSampleCount);
if (window.Count > verification.StableSampleCount) window.RemoveAt(0);
if (window.Count >= verification.StableSampleCount) { /* 范围比较 */ }
await execution.DelayAsync(FineTuneDelayKind.StableRead, verification.StableSampleIntervalMs, ct);
```

范围比较使用 `verification.StableRangeMm`。`EnsureAbsFeedbackFollowedAsync` 的所有重读也传递同一个快照。

- [ ] **Step 3: 保留反馈跟随判断的历史 1mm 下限**

将旧 `StableReadToleranceMm` 拆为私有常量 `AbsFeedbackFollowMinimumToleranceMm = 1`，仅供：

```csharp
int allowedError = Math.Max(axis.Tolerance, AbsFeedbackFollowMinimumToleranceMm);
```

使用。这样页面的“稳定范围”只控制用户指定的稳定读取标准，不会意外放宽或收紧“微调后显示/Abs 必须跟随”的安全判断。

- [ ] **Step 4: 更新既有微调测试的默认调用序列**

将原有 `delay:Phase:500`、`delay:Phase:600`、`AddStableReadCalls(...5...)` 断言改为读取 `ActiveConfig().AbsFineTuneVerification` 的默认 `1200`、`1200`、`3`、`200`。保留最大 20 次、最多两次微调、三次反馈刷新、取消和原始异常消息的现有断言。

- [ ] **Step 5: 执行全部微调定向测试，确认通过**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter "FullyQualifiedName~FineTuneInstrumentationContractTests|FullyQualifiedName~AbsFineTuneVerificationConfigContractTests"`

Expected: PASS；自定义配置的调用顺序生效，原安全异常与重读上限未变化。

### Task 4: 更新配置说明并完成验证

**Files:**
- Modify: `Docs/全线业务逻辑详解.md`（绝对编码器微调段）
- Modify: `Docs/03-设备交互流程/整线引擎流程总览_按代码.md`（天车微调读取策略段）

- [ ] **Step 1: 更新两份运行说明**

明确以下事实：配置对全部天车共享；当前推荐默认值为前等待 1200ms、3 次/200ms、窗口范围 <=1mm、微调后最短 1200ms；配置页修改会影响后续动作，保存后落盘；稳定判断是最大值减最小值而非逐值全等；最大读取次数仍为 20。

- [ ] **Step 2: 构建主项目**

Run: `dotnet build AutomaticOnlineHostComputer.csproj`

Expected: Build succeeded，零错误。

- [ ] **Step 3: 运行完整测试项目**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-build`

Expected: 全部测试通过；若存在与本改动无关的既有失败，记录测试名与输出，不掩盖或修改无关代码。

- [ ] **Step 4: 检查工作区差异**

Run: `git diff -- Infrastructure/Config/MotionConfig.cs Config/motion_settings.json Presentation/ViewModels/Config/ConfigPageViewModel.cs Views/Config/ConfigPageView.xaml Service/FlowEngine/XAbsFineTuneHelper.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/FineTuneInstrumentationContractTests.cs AutomaticOnlineHostComputer.Tests/FlowEngine/AbsFineTuneVerificationConfigContractTests.cs Docs/全线业务逻辑详解.md Docs/03-设备交互流程/整线引擎流程总览_按代码.md`

Expected: 仅包含本计划的共享微调验证配置、页面、测试和说明变更；不触碰 ST714 的 `-1` 语义。
