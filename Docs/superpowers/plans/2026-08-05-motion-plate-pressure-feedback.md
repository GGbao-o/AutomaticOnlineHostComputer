# 运动参数页有板与下压反馈 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在手动运动参数页显示 X11 有板与 X2 下压限位的单次实际反馈。

**Architecture:** 扩展 `CraneManualControlViewModel` 的既有 X6/X7 读取和空值安全语义，以同一个 `CraneService` 读四个线圈。XAML 在现有状态卡中新增两个绑定；不创建轮询或写入路径。

**Tech Stack:** C#、WPF/XAML、xUnit、Modbus X 线圈读取。

---

### Task 1: 写入并验证状态反馈契约

**Files:**
- Modify: `AutomaticOnlineHostComputer.Tests/Presentation/ManualMotionMagnetFeedbackContractTests.cs`
- Test: `AutomaticOnlineHostComputer.Tests/Presentation/ManualMotionMagnetFeedbackContractTests.cs`

- [ ] **Step 1: 写失败测试**

在源代码契约测试中断言 ViewModel 使用 `CraneAddress.D_X11_HasPlate` 和 `CraneAddress.D_X2_MagnetLimit`，并存在 `bool?` 的有板、下压状态和状态文案；在 XAML 中断言两个文案绑定存在。

- [ ] **Step 2: 运行测试并确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~ManualMotionMagnetFeedbackContractTests --no-restore`

Expected: FAIL，因为生产代码尚未引用 X11/X2 或未显示两项状态。

- [ ] **Step 3: 实现最小生产代码**

扩展现有 X6/X7 的可空状态、刷新读取、异常回退和设备切换清空逻辑，以同一个 `TryReadMagnetFeedbackAsync` 读取 X11/X2。

- [ ] **Step 4: 再次运行聚焦测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~ManualMotionMagnetFeedbackContractTests --no-restore`

Expected: PASS，且输出显示 0 个失败。

### Task 2: 更新页面与回归验证

**Files:**
- Modify: `Views/Motion/Controls/ManualMotionControlPanel.xaml`
- Modify: `Presentation/ViewModels/Home/CraneManualControlViewModel.cs`
- Test: `AutomaticOnlineHostComputer.Tests/Presentation/ManualMotionMagnetFeedbackContractTests.cs`

- [ ] **Step 1: 更新状态卡**

在现有 `WrapPanel` 中新增 X11 和 X2 的独立状态标签：X11=1 为绿色、X11=0 为中性灰色；X2=1 为红色的下压触发提示、X2=0 为绿色；未知均为灰色。继续绑定已有 `RefreshTargetCommand`。

- [ ] **Step 2: 验证构建和全量测试**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: 生成成功，0 个错误。

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-build --no-restore`

Expected: 全部测试通过，0 个失败。

- [ ] **Step 3: 检查提交范围**

Run: `git diff --check -- Presentation/ViewModels/Home/CraneManualControlViewModel.cs Views/Motion/Controls/ManualMotionControlPanel.xaml AutomaticOnlineHostComputer.Tests/Presentation/ManualMotionMagnetFeedbackContractTests.cs`

Expected: 无空白错误；仅提交上述生产代码和测试文件。
