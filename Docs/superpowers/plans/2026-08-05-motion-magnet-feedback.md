# Motion Page Magnet Feedback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Display the selected motion-control device's actual X6 charge and X7 demagnetize feedback beside device selection without adding a new PLC connection or background poller.

**Architecture:** Extend `CraneManualControlViewModel`'s existing coalesced position refresh to read X6 and X7 from the same cached `CraneService`. Nullable properties retain the distinction between an explicit PLC `0` and an unreadable input. The motion panel binds two independent feedback chips and routes its status-refresh button through the existing refresh command.

**Tech Stack:** WPF XAML, C# / .NET 8, CommunityToolkit `ObservableObject`, xUnit source-contract tests, existing `CraneService` Modbus client.

---

### Task 1: Lock the display and feedback-read contract

**Files:**

- Create: `AutomaticOnlineHostComputer.Tests/Presentation/ManualMotionMagnetFeedbackContractTests.cs`

- [ ] **Step 1: Add source-contract coverage for both PLC feedbacks and the panel bindings**

```csharp
namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class ManualMotionMagnetFeedbackContractTests
{
    [Fact]
    public void Manual_motion_refresh_reads_both_actual_magnet_feedbacks_and_keeps_unknown_distinct()
    {
        string source = Read("Presentation", "ViewModels", "Home", "CraneManualControlViewModel.cs");

        Assert.Contains("CraneAddress.D_X6_MagnetizeOk", source, StringComparison.Ordinal);
        Assert.Contains("CraneAddress.D_X7_DemagnetizeOk", source, StringComparison.Ordinal);
        Assert.Contains("public bool? IsMagnetizeFeedback", source, StringComparison.Ordinal);
        Assert.Contains("public bool? IsDemagnetizeFeedback", source, StringComparison.Ordinal);
        Assert.Contains("return null;", source, StringComparison.Ordinal);
        Assert.Contains("RequestRefreshTargetsFromCurrent();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Motion_panel_shows_independent_charge_and_demagnetize_feedback_with_refresh()
    {
        string source = Read("Views", "Motion", "Controls", "ManualMotionControlPanel.xaml");

        Assert.Contains("磁铁实际反馈", source, StringComparison.Ordinal);
        Assert.Contains("ManualControl.MagnetizeFeedbackText", source, StringComparison.Ordinal);
        Assert.Contains("ManualControl.DemagnetizeFeedbackText", source, StringComparison.Ordinal);
        Assert.Contains("刷新磁铁状态", source, StringComparison.Ordinal);
        Assert.Contains("ManualControl.RefreshTargetCommand", source, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(parts)));
}
```

- [ ] **Step 2: Run the new contract before implementation**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~ManualMotionMagnetFeedbackContractTests`

Expected: FAIL because the feedback properties and the status card do not exist.

### Task 2: Read actual X6/X7 feedback in the shared manual-control view model

**Files:**

- Modify: `Presentation/ViewModels/Home/CraneManualControlViewModel.cs:62-78,400-405,670-694,554-569`

- [ ] **Step 1: Add nullable feedback state and derived operator text**

```csharp
private bool? _isMagnetizeFeedback;
public bool? IsMagnetizeFeedback
{
    get => _isMagnetizeFeedback;
    private set
    {
        if (SetField(ref _isMagnetizeFeedback, value))
            OnPropertyChanged(nameof(MagnetizeFeedbackText));
    }
}

private bool? _isDemagnetizeFeedback;
public bool? IsDemagnetizeFeedback
{
    get => _isDemagnetizeFeedback;
    private set
    {
        if (SetField(ref _isDemagnetizeFeedback, value))
            OnPropertyChanged(nameof(DemagnetizeFeedbackText));
    }
}

public string MagnetizeFeedbackText => IsMagnetizeFeedback switch
{
    true => "充磁到位（X6=1）",
    false => "充磁未到位（X6=0）",
    null => "充磁读取未知"
};

public string DemagnetizeFeedbackText => IsDemagnetizeFeedback switch
{
    true => "退磁到位（X7=1）",
    false => "退磁未到位（X7=0）",
    null => "退磁读取未知"
};
```

- [ ] **Step 2: Clear stale values on device change and read X6/X7 during the coalesced refresh**

```csharp
// In SelectedDevice after notifications, before queuing the existing refresh:
IsMagnetizeFeedback = null;
IsDemagnetizeFeedback = null;
RequestRefreshTargetsFromCurrent();

// In RefreshTargetsFromCurrentAsync after the existing ReadStatusAsync call:
if (status != null)
{
    AbsXTarget = IsCraneOnly ? status.XPos : 0;
    AbsYTarget = status.YPos;
    AbsZTarget = status.ZPos;
}

IsMagnetizeFeedback = await TryReadMagnetFeedbackAsync(
    service, CraneAddress.D_X6_MagnetizeOk, "X6充磁到位", cts.Token);
IsDemagnetizeFeedback = await TryReadMagnetFeedbackAsync(
    service, CraneAddress.D_X7_DemagnetizeOk, "X7退磁到位", cts.Token);
```

- [ ] **Step 3: Add the non-guessing feedback reader and refresh after successful manual magnet commands**

```csharp
private async Task<bool?> TryReadMagnetFeedbackAsync(
    CraneService service, int address, string label, CancellationToken ct)
{
    try
    {
        return await service.ReadXBitAsync(address, ct);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[CraneManualVM] [{CurrentDeviceName}] {label}读取失败: {ex.Message}");
        return null;
    }
}

// At the end of both MagnetOnAsync and MagnetOffAsync after their service call succeeds:
RequestRefreshTargetsFromCurrent();
```

- [ ] **Step 4: Increase only the manual read timeout enough for status plus two coil reads**

```csharp
private static readonly TimeSpan RefreshPositionTimeout = TimeSpan.FromSeconds(2);
```

### Task 3: Add the status card to the motion-parameter panel

**Files:**

- Modify: `Views/Motion/Controls/ManualMotionControlPanel.xaml:116-139`

- [ ] **Step 1: Extend `设备选择` with independent X6/X7 chips and a refresh button**

```xml
<StackPanel Grid.Column="1" Margin="10,0,0,0" VerticalAlignment="Center">
    <TextBlock Text="{Binding ManualControl.SelectedDeviceDisplay}" TextWrapping="Wrap" />
    <StackPanel Margin="0,6,0,0" Orientation="Horizontal">
        <TextBlock FontWeight="SemiBold" Text="磁铁实际反馈：" />
        <TextBlock Margin="0,0,12,0" Text="{Binding ManualControl.MagnetizeFeedbackText}" />
        <TextBlock Margin="0,0,12,0" Text="{Binding ManualControl.DemagnetizeFeedbackText}" />
        <Button Height="24"
                Padding="8,0"
                Command="{Binding ManualControl.RefreshTargetCommand}"
                Content="刷新磁铁状态" />
    </StackPanel>
</StackPanel>
```

- [ ] **Step 2: Add `DataTrigger` styles to each feedback chip**

```xml
<TextBlock Text="{Binding ManualControl.MagnetizeFeedbackText}">
    <TextBlock.Style>
        <Style TargetType="TextBlock">
            <Setter Property="Foreground" Value="#6B7280" />
            <Style.Triggers>
                <DataTrigger Binding="{Binding ManualControl.IsMagnetizeFeedback}" Value="True">
                    <Setter Property="Foreground" Value="#16803C" />
                </DataTrigger>
                <DataTrigger Binding="{Binding ManualControl.IsMagnetizeFeedback}" Value="False">
                    <Setter Property="Foreground" Value="#9A6700" />
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </TextBlock.Style>
</TextBlock>
```

Use the identical structure for `DemagnetizeFeedbackText`, changing only its two bindings to `IsDemagnetizeFeedback`.

### Task 4: Verify the bounded UI addition

**Files:**

- Create: `AutomaticOnlineHostComputer.Tests/Presentation/ManualMotionMagnetFeedbackContractTests.cs`
- Modify: `Presentation/ViewModels/Home/CraneManualControlViewModel.cs`
- Modify: `Views/Motion/Controls/ManualMotionControlPanel.xaml`

- [ ] **Step 1: Run the focused contract after implementation**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~ManualMotionMagnetFeedbackContractTests`

Expected: PASS; both X inputs, nullable unknown handling, and panel bindings are present.

- [ ] **Step 2: Build the WPF application**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: Build succeeded with 0 errors.

- [ ] **Step 3: Run the full suite and inspect whitespace**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-build --no-restore`

Expected: PASS with 0 failed tests.

Run: `git diff --check -- Presentation/ViewModels/Home/CraneManualControlViewModel.cs Views/Motion/Controls/ManualMotionControlPanel.xaml`

Expected: no whitespace errors.
