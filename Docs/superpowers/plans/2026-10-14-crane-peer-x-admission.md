# 前后天车持锁等待对方X实施计划

**目标：** 在1/3号前天车打号前，以及2/4号后天车上料拿齐区域锁后，实时检查对方X；不满足时持锁无限等，人工移动后满足即可继续。

**方案：** 使用共用的只读坐标准入组件，四个调用入口保留原锁归属、取消和异常收尾。四个独立阈值放入共享MotionConfig并在配置页“其他”展示，轮询时重新读取阈值。比较实际显示X，不加坐标偏移。

**技术：** C#/.NET 8、WPF绑定、System.Text.Json、xUnit；逐文件apply_patch，不用Python更新代码。按用户授权在当前会话直接实施，不派发子代理。

## 已确认规则

| 当前天车 | 读取对方 | 放行条件及默认值 | 持锁范围 |
|---|---|---|---|
| 1 | 2 | X >= -14000 | ZoneMT |
| 2 | 1 | X <= 2500 | ZoneMT+ZoneTS |
| 3 | 4 | X >= -14000 | ZoneMT |
| 4 | 3 | X <= 2500 | ZoneMT+ZoneTS |

成功读到但不满足时，每500ms异步等，不释放锁、不设业务超时、不丢板、不发运动或磁铁命令。通信失败不能当作0或旧坐标放行，抛异常交原catch/finally。停止/取消仍响应原令牌。前端保留现有空位和暂停判断，不新增ZoneTS后的坐标判断；后端下料不接入本次入口。

## 实施步骤

- [x] 记录修改前全量测试结果，保存TRX，对照已有失败。
- [x] 新增`AutomaticOnlineHostComputer.Tests/FlowEngine/CranePeerXGateTests.cs`：覆盖四方向等号/不满足、无限轮询后放行、实时阈值修改、读取失败、取消以及四处入口顺序。
- [x] 新增`Service/FlowEngine/CranePeerXGate.cs`：生产适配实时ReadStatusAsync；可注入读取/延时验证控制流；首次等待、每20轮心跳及放行日志，不占用或释放调度锁。
- [x] 修改`Infrastructure/Config/MotionConfig.cs`和`Config/motion_settings.json`：增加`cranePeerXAdmission`及四个有符号整型阈值，兼容旧文件缺项。
- [x] 在两线Front的原暂停判断之后、MarkingHandshakeAsync之前接入；在两线Rear上料拿齐ZoneMT/ZoneTS之后、原来源复核之前接入。为四个入口和组件写中文注释。
- [x] 修改`Presentation/ViewModels/Config/ConfigPageViewModel.cs`、`Views/Config/ConfigPageView.xaml`，在“其他”天车归位区下方展示四个条件，使用现有Save保存。
- [x] 更新运维文档、原ZoneMT实施记录；运行新增测试、相关契约及全量测试，与基线比较；主项目构建、XAML解析、git diff --check。
- [x] 检查本轮diff，排除用户原有配置和手动注释，只提交本轮改动。提交格式`feat(flow-engine): ...`，不push。

## 验证命令

```powershell
dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --verbosity quiet -p:WarningLevel=0 --filter FullyQualifiedName~CranePeerX -- RunConfiguration.TargetPlatform=x86
dotnet build AutomaticOnlineHostComputer.csproj --no-restore --verbosity quiet -p:WarningLevel=0
git diff --check
```

测试不连接真实PLC；实际机械位置是否允许通行由现场阈值和工人判断。本次不修改已有设备动作、异常恢复策略和其他区域准入。
