# TODO — 当前位置字段直写 PLC 寄存器

> 状态：待实现（需确认坐标系对齐后开放）

## 背景

天车管理和机器管理页面已新增 `current_x / current_y / current_z` 字段（数据库 + CRUD + ViewModel + 编辑弹窗）。

用户在管理页面编辑"当前位置"后，点击保存会写入数据库。未来需要同时写入 PLC 寄存器，
让天车/机械手直接走到这个坐标。

## 实现方案

### 天车管理 — 编辑 current_x/y/z 后

```
保存按钮 → ManagementUpdateService.UpdateCraneAsync()
  ↓ 同时
CraneService.MoveAbsoluteAsync(currentX, currentY, currentZ)
  ↓
写 D3102~D3107（目标坐标 DINT）→ 触发 D4520~D4522 → 轮询到位 → 复位

坐标映射：
  crane.current_x → D3102~D3103 (DINT)
  crane.current_y → D3104~D3105 (DINT)
  crane.current_z → D3106~D3107 (DINT)
```

### 机器管理 — 编辑 current_x/y/z 后

```
保存按钮 → ManagementUpdateService.UpdateMachineAsync()
  ↓ 同时
CraneService.MoveAbsoluteAsync(currentX, currentY, currentZ)
  ↓
同上（机器坐标不直接控制天车，需先选择天车再执行）
```

## 前置条件

1. [ ] 确认数据库 `current_x/y/z` 的坐标系与 PLC 坐标系一致
2. [ ] 确认天车/机械手的编码器原点与数据库标定原点对齐
3. [ ] 在编辑弹窗上加一个「保存并移动」按钮（区别于普通「保存」）
4. [ ] 移动前必须通过安全检查 `CheckSafetyAsync()`
5. [ ] 移动前必须先切换到手动模式 `EnsureManualModeAsync()`

## 安全注意事项

- 编辑 `current_z` 要特别注意 Z 轴方向（上/下），防止误降撞机
- 建议加二次确认弹窗："确认将天车移动到 X=xxx Y=xxx Z=xxx？"
- 移动过程中禁止编辑其他字段
- 超时 30 秒未到位 → 报警 + 复位触发

## 涉及文件

| 文件 | 改动 |
|------|------|
| `AddCraneDialog.xaml.cs` | 保存时加调用 `MoveAbsoluteAsync` |
| `AddMachineDialog.xaml.cs` | 保存时加调用逻辑（选天车后执行） |
| `CraneManagementView.xaml` | DataGrid 加 current_x/y/z 列 |
| `MachineManagementView.xaml` | DataGrid 加 current_x/y/z 列 |
| `AddCraneDialog.xaml` | 加 current_x/y/z 输入框 +「保存并移动」按钮 |
| `AddMachineDialog.xaml` | 加 current_x/y/z 输入框 |
