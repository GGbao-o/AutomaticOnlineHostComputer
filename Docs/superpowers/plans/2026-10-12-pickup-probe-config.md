# 取板探测配置 Implementation Plan

用户授权继续执行；本会话按以下步骤完成，不额外派发代理。

**Goal:** 将取板位置数、补偿步长开放为全线共用配置，默认不变。

**Architecture:** MotionConfig保存两项整数；页面直接更新共享配置并预览累计距离；14入口传不可变PickupProbeSettings，组件校验后使用本次快照。

**Tech Stack:** C#、WPF、System.Text.Json、xUnit，沿用现有配置和设备接口。

## 1. 配置与组件回归测试

- [x] 修改`AutomaticOnlineHostComputer.Tests/FlowEngine/PickupProbeHelperTests.cs`：增加可选Settings假设备输入，断言`new(4,2)`产生`100/102/104/106`且只充磁一次；`new(1,5)`无补偿；非法配置无运动；1mm步长不提前确认；修改共享配置不改变本次快照。
- [x] 新增`AutomaticOnlineHostComputer.Tests/FlowEngine/PickupProbeConfigTests.cs`：验证默认3/5，旧JSON默认，新JSON往返、页面绑定/预览、14生产入口显式传快照。
- [x] 运行`dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter 'FullyQualifiedName~PickupProbe' -p:WarningLevel=0 -- RunConfiguration.TargetPlatform=x86`，先确认新增接口尚未实现的编译失败，再实现并重跑。

## 2. 实现配置和快照

- [x] `Infrastructure/Config/MotionConfig.cs`的GrindingSection增加`public int PickupProbeCount { get; set; } = 3;`及`public int PickupProbeStepMm { get; set; } = 5;`，带详细中文注释。
- [x] `Config/motion_settings.json`只增加`"pickupProbeCount": 3`、`"pickupProbeStepMm": 5`。
- [x] `Presentation/ViewModels/Config/ConfigPageViewModel.cs`增加两个共享配置属性及`PickupProbeSummary`，两项修改均通知预览；非法输入预览提示停止取料。
- [x] `Views/Config/ConfigPageView.xaml`在全线X11参数旁增加两个输入框、预览和生效时机说明。
- [x] `Service/FlowEngine/PickupProbeHelper.cs`增加不可变记录`PickupProbeSettings(int PositionCount, int StepMm)`；默认`new(3,5)`；校验正整数及`checked((PositionCount-1)*StepMm)`，在设备IO前拒绝非法值。
- [x] 两个PickAsync重载增加可选settings参数。上界改为`checked(z0+maxExtraDescentMm)`，循环和目标使用配置，日志改为实际次数/步长/上限；步长1mm到位容差0，其余1mm。

## 3. 全线接入

- [x] `Line1FrontFlowEngine.cs`、`Line2FrontFlowEngine.cs`各3入口，`Line1RearFlowEngine.cs`、`Line2RearFlowEngine.cs`、`GrindingFlowEngine.cs`、`BalancingFlowEngine.cs`各2入口，均添加`settings: new(_cfg.Grinding.PickupProbeCount, _cfg.Grinding.PickupProbeStepMm)`，保留既有moveInitial和validateAction。
- [x] 修正受影响入口中“三位置/5/10”的固定表述为配置或默认表述，不更改异常catch/finally和业务握手。

## 4. 验证、文档、提交

- [x] 跑探测相关测试、主项目构建、完整测试，对照现有471项/8个失败名称基线，避免把原用户配置差异当作本轮失败。
- [x] 更新`Docs/运维/X11充磁前探测与异常作废.md`及配置说明，增加参数、生效时机、默认例子、非法值和实际补偿上限说明。
- [x] `git diff --check`，核对4个用户原修改保留；仅暂存本次JSON新增字段，源码与Docs显式暂存。
- [x] 本地commit，不push，报告测试结果及配置页面位置。
