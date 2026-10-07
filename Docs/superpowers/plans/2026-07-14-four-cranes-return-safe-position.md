# 四台天车一键回安全位置 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Execute this plan inline, task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Do not create Git commits: the user explicitly requested no Git submission.

**Goal:** 在现有手动“天车移动”窗口中，让操作员一键、串行地将1~4号天车回到配置的安全 X 位置，并在任何移动前统一验证四台 Z 均处于0±5mm。

**Architecture:** 功能仅增加到 `CraneMoveDialog` 的 XAML 和代码后台。代码后台复用注入的 `CraneConnectionCache`、共享 `MotionConfig` 以及 `CraneService` 的既有连接、设速、绝对 X 运动和到位轮询，不创建新连接或新业务服务。批量操作只管理本窗口控件状态，不读取或修改引擎、队列、缓存、工件、X11或自动流程锁。

**Tech Stack:** C# 12、.NET 8、WPF、async/await、现有 Modbus TCP `CraneService`。

---

## 文件结构

- 修改 `Views/Home/Dialogs/CraneMoveDialog.xaml`：增加批量状态文本、批量回位按钮、关闭按钮命名和执行中关闭保护事件。
- 修改 `Views/Home/Dialogs/CraneMoveDialog.xaml.cs`：实现四台统一Z预检、1→2→3→4串行X回位、失败汇总、执行中控件禁用。
- 不新增 PLC 地址、配置字段、引擎或 ViewModel 命令。

### Task 1: 增加手动窗口批量回位控件

**Files:**
- Modify: `Views/Home/Dialogs/CraneMoveDialog.xaml:39-135`

- [ ] **Step 1: 在移动信息区增加只读批量状态文本，并在底部增加批量按钮。**

  将底部控制区改为垂直布局：上方显示 `TxtBatchStatus`，初始文本为“批量回位待命：将先检查1~4号天车 Z=0±5mm”；下方保留单台移动和关闭按钮，并新增：

  ```xml
  <Button
      x:Name="BtnReturnSafePositions"
      Width="160"
      Height="32"
      Margin="0,0,12,0"
      Click="BtnReturnSafePositions_Click"
      Content="4台回安全位置"
      FontWeight="Bold" />
  ```

  给关闭按钮增加 `x:Name="BtnClose"`，给窗口增加 `Closing="Window_Closing"`。必要时把窗口高度由600调整为650，确保底部状态文字不被遮挡。

- [ ] **Step 2: 目检 XAML 结构。**

  确认单台“确认移动 X 轴”仍存在、批量按钮位于同一手动窗口、研磨天车没有出现在新控件中，且所有新增 `x:Name` 与事件名唯一。

### Task 2: 实现统一 Z 预检和串行回位

**Files:**
- Modify: `Views/Home/Dialogs/CraneMoveDialog.xaml.cs:14-279`

- [ ] **Step 1: 添加批量操作常量、目标天车集合和窗口状态辅助方法。**

  在 `CraneMoveDialog` 中定义：

  ```csharp
  private const int BatchSafeZTolerance = 5;
  private static readonly int[] BatchCraneNos = { 1, 2, 3, 4 };
  private bool _isBatchReturning;

  private void SetBatchOperationUi(bool running)
  {
      _isBatchReturning = running;
      CmbCrane.IsEnabled = !running;
      CmbStation.IsEnabled = !running;
      BtnRefresh.IsEnabled = !running;
      BtnMove.IsEnabled = !running;
      BtnReturnSafePositions.IsEnabled = !running;
      BtnClose.IsEnabled = !running;
      if (!running) UpdateTargetInfo();
  }
  ```

  此方法只锁定本窗口，不能访问或暂停任一自动引擎。

- [ ] **Step 2: 实现“全有或全无”的 Z 预检。**

  在 `BtnReturnSafePositions_Click` 的开始，按 `BatchCraneNos` 顺序调用 `_craneCache.GetOrCreateService(craneNo)`；未连接则使用既有 `ConnectAsync()`，随后用单台5秒取消令牌调用 `ReadStatusAsync()`。

  对每台记录下列任一失败：读取结果为 `null`、连接/读取异常、或 `Math.Abs(status.ZPos) > BatchSafeZTolerance`。预检完成后若失败集合非空，显示：

  ```text
  以下天车未通过回安全位置预检，未执行任何移动：
  - 1号线前天车：Z=6mm，请先回原点（0±5mm）
  - 2号线后天车：读取失败：...
  ```

  然后返回。预检阶段不得调用 `SetAbsSpeedAsync` 或 `MoveAbsoluteAsync`，保证任一失败时四台均未收到移动命令。

- [ ] **Step 3: 通过预检后按固定顺序串行移动。**

  创建总时限20分钟的 `CancellationTokenSource`。对1、2、3、4依次执行：

  ```csharp
  var crane = _craneCache.GetOrCreateService(craneNo);
  var speed = _cfg.GetCraneSpeed(craneNo);
  await crane.SetAbsSpeedAsync(
      speed.X.Speed, speed.X.Accel, speed.X.Decel,
      speed.Y.Speed, speed.Y.Accel, speed.Y.Decel,
      speed.Z.Speed, speed.Z.Accel, speed.Z.Decel,
      cts.Token);

  int targetX = _cfg.GetCraneHomeX(craneNo);
  await crane.MoveAbsoluteAsync(targetX, -1, -1, ct: cts.Token);
  ```

  每台开始前更新 `TxtBatchStatus` 为“正在回位：{天车名称} → X={targetX}”；成功后累积完成记录。不要向Y或Z写目标，不自动Z回零，不检查引擎状态、X11或自动流程锁。

- [ ] **Step 4: 在失败时停止后续，而不反向移动已完成天车。**

  捕获当前台的连接、设速、运动、超时或到位异常，立刻停止循环。弹窗列出已完成天车、失败天车和原始异常消息；不重试、不发后续天车命令、不向任何已完成天车发反向命令。

  全部完成时将 `TxtBatchStatus` 更新为“4台天车已回配置安全位置”，并显示完成提示。

- [ ] **Step 5: 防止窗口在执行中被关闭。**

  实现：

  ```csharp
  private void Window_Closing(object? sender, CancelEventArgs e)
  {
      if (!_isBatchReturning) return;
      e.Cancel = true;
      MessageBox.Show("四台天车正在回安全位置，请等待本次操作结束。",
          "操作进行中", MessageBoxButton.OK, MessageBoxImage.Information);
  }
  ```

  添加 `using System.ComponentModel;`。在 `finally` 中调用 `SetBatchOperationUi(false)`，确保成功、预检拒绝或异常后按钮恢复。

### Task 3: 构建与人工验证

**Files:**
- Verify only: `AutomaticOnlineHostComputer.csproj`

- [ ] **Step 1: 编译。**

  Run:

  ```powershell
  dotnet build AutomaticOnlineHostComputer.csproj --no-restore -clp:ErrorsOnly
  ```

  Expected: exit code 0；不新增编译错误。

- [ ] **Step 2: 代码审查关键边界。**

  检查批量入口只引用 `CraneMoveDialog`、`CraneConnectionCache`、`MotionConfig` 和 `CraneService`；确认无 `Line1/Line2/Grinding` 引擎、无任务队列/缓存字段、无 PLC 新地址、无 X11 读写。

- [ ] **Step 3: 现场人工验证清单。**

  1. 四台 Z 都在0±5mm：点击后依次看到1、2、3、4号天车仅X轴回到当前配置值。
  2. 任意一台Z=6mm：弹窗指出天车和Z值，四台均不移动。
  3. 任意一台状态读取失败：弹窗说明读取失败，四台均不移动。
  4. 将某台移动制造失败：之后的天车不下发命令，之前成功者保持当前位置，窗口显示完成/失败明细。
