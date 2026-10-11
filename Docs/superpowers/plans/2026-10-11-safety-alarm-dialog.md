# Safety Alarm Dialog Implementation Plan

> 在当前会话逐项执行；设计已获用户确认，不再等待设计或执行方式确认。

**Goal:** 六类斜床/研磨上下料异常采用统一完整说明，生产业务行为不变。

**Architecture:** 展示上下文通过 AsyncLocal 随单次动作传播，复制工件和失败步骤并在原清理后补充结果。只读短连接采样与UI异步运行，统一窗口按动作编号复用，关闭、刷新和复制均不影响引擎。

**Tech Stack:** C#、.NET 8、WPF、xUnit、已有Modbus只读API。

## 1. 展示模型及验证

- [x] 新增 `Service/Alarms/SafetyAlarmDetails.cs` 和 `Service/FlowEngine/FlowAlarmPresentationContext.cs`：不可变失败快照、状态变化通知、步骤中文说明、工位名称、独立清理结果。
- [x] 新增 `AutomaticOnlineHostComputer.Tests/Presentation/SafetyAlarmPresentationTests.cs`：验证工件清空后身份仍在、未知不变0、失败步骤不被回位覆盖、清零失败不显示成功。
- [x] 运行 `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter SafetyAlarm`；新增行为测试应先失败，实现后通过。

## 2. 只读采样

- [x] 新增 `Service/Alarms/CraneAlarmSnapshotReader.cs`：通过天车编号获取IP，独立临时连接，仅读取D5000起30字及X2/X6/X7/X11，总时限2秒，失败保留部分读取并注明时间、原因。
- [x] 验证负数AbsX/AbsY、无符号Y/Z、部分失败、超时及禁止设备写入；不修改生产通信客户端或天车业务服务。

## 3. 六类流程信息接入

- [x] 修改 `Line1RearFlowEngine.cs`、`Line2RearFlowEngine.cs`、`GrindingFlowEngine.cs`：动作开始建立展示上下文；外层异常复制失败事实；现有清理完成后只记录展示结果；结束作用域记录最终锁状态。
- [x] 用源码比较验证新增语句之外的原业务语句保持一致，现有清理及缓存契约测试继续运行。

## 4. 统一窗口

- [x] 新增 `SafetyAlarmDialogViewModel.cs`、`SafetyAlarmDialog.xaml`、`SafetyAlarmDialog.xaml.cs`：按批准布局显示异常、工件流向、历史与实时状态、最终收尾，三个只读按钮。
- [x] 修改 `HomeViewModel.cs`：有展示上下文的六类动作使用新窗口；原暂停和记录顺序保留；同一动作更新窗口，不同动作独立显示，其余告警不变。

## 5. 完成验证

- [x] 构建WPF项目；运行新增测试、业务回归及全部测试；区分基线失败与新增失败。
- [x] 在不连接设备的预览进程中检查窗口显示、滚动、复制、关闭和异步状态更新。
- [x] 检查git差异并限定本次提交清单，不包含用户已有修改；执行commit，不push。

## 验证记录

- 基线：387项测试，377通过、10失败。最终：400项测试，390通过、同样10失败，新增13项全部通过。
- 定向验证：快照、只读采样、弹窗模型、WPF布局及原告警顺序28项通过。
- 三个流程引擎：去除新增展示语句后，与HEAD源码逐字一致；设备写入、分支、缓存清理、暂停、恢复及锁释放原语句均未修改。
- WPF Release构建通过；离屏预览使用假读取接口，未启动生产应用、未连接真实PLC；复制内容验证包含各区域完整文本，未修改用户剪贴板。
- 实时采样使用独立临时连接，全部新增通信调用仅为连接、读取和销毁；2秒总时限，未知不显示成0。关闭时取消采样并解除上下文订阅。
- 同一动作关闭后不因finally报告重新弹出；不同动作独立显示。研磨缓存未记录工件来源线路时明确说明未记录。
- 原有失败涉及配置页面/配置契约、旧应急恢复契约、分流及业务文档契约，未在本次弹窗改动中修复。
- 结果文件：`output/alarm-dialog-verification/safety-alarm-full.trx`；预览：Codex visualizations目录内`safety-alarm-dialog.png`。
