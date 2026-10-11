# 配置生效与斜床/研磨XY微调核对

> 核对日期：2026-10-11。以MotionConfig、ConfigPageViewModel、XAbsFineTuneHelper及各引擎调用为依据。当前工作区JSON数值是核对时快照，不能冒充所有部署机的运行值。

## 1. 更改配置何时生效

| 更改方式 | 运行中内存 | 保存/下一次加载 |
|---|---|---|
| 在配置页编辑且绑定已提交到ViewModel/行模型 | 修改共享MotionConfig，后续读取可见，线路运行时也一样 | Save持久化；未保存值重启丢失 |
| 外部编辑程序运行目录Config/motion_settings.json | 不自动生效：没有文件监视或Reload入口 | 下次HomeViewModel加载时读取 |
| 外部编辑仓库Config/motion_settings.json | 不自动生效，也不保证是实际运行文件 | 构建/复制发布后还要核实运行目录文件与重新加载 |
| 改数据库工位坐标/偏移 | 不等同于修改绝对标定配置；各引擎有已加载缓存或初始化局部字段 | 以各具体重新加载/初始化入口为准，不能宣称所有在途动作立即改变 |

HomeViewModel构造调用MotionConfig.Load；CreateConfigPageViewModel把同一_cfg传给配置页，各引擎也共享该对象。Load基于AppDomain.CurrentDomain.BaseDirectory，不按当前PowerShell目录查找。Save序列化共享内存到同目录临时文件，验证JSON后替换正式文件并保留.bak；Save不是“重新读取外部刚改的文件”。

“即时可见”不表示已发送运动目标会改变。主移动坐标、safeZ、速度等一旦读入局部变量或发往PLC，本次已发送命令不撤销；下一个读取点才可能见新值。一次微调开始时固定轴目标、方向、容差/最大修正和共享验证参数；后续某些服务超时/其它动作参数仍在实际使用点读取，整趟搬运不是所有配置的原子快照。

## 2. 三种坐标不能混用

- 数据库显示坐标及X/Y/Z偏移：用于主绝对移动目标。
- xAbsFineTune/yAbsFineTune.stationTargets[天车号][站号]：对应工位绝对编码器标定值，用于到位后的校正。
- 斜床长度/孔型计算的yOff：顶尖中心与实际取板位的几何偏移；下料时会转换成动态AbsY目标。

YEncoderAbs是有符号INT16（D5020），允许负数；显示Y是D5022无符号值。缺少标定、标定=-1、轴Enabled=false分别跳过该轴；其中-1是禁用哨兵，不是可用标定数值。标定以正确天车号和站号匹配，不能把天车3打号ST502的标定用于天车4斜床ST606。

## 3. Y方向与动态目标

| 天车 | 线路角色 | 默认/当前JSON的AbsY随显示Y方向 |
|---|---|---|
| 1 | 1号线前天车 | +1 |
| 2 | 1号线后天车 | -1 |
| 3 | 2号线前天车 | +1 |
| 4 | 2号线后天车 | +1 |
| 5 | 共用研磨 | -1（代码历史回退值，现场方向应实际核对） |

absolutePerDisplayDirections只能配置±1；配置缺失用helper回退方向，非法方向抛异常。X方向固定+1。

```text
偏差Abs = 目标Abs - 当前Abs
修正显示值 = 当前显示值 + 偏差Abs × 方向
斜床下料动态目标AbsY = 基准AbsY - 方向 × yOff
yOff = (centerDistances[站号] - 板长) / 2 + 孔型偏移
```

ComputeYOffset以BoreType==100选LargeBoreOffset，其它值选SmallBoreOffset，结果截断为int；不能直接把研磨“1=大孔/2=小孔”的PLC写码当作这里的100。yOff传入微调helper时必须非负，否则报错。

| 调用位置 | 是否传yCenterToPickOffsetMm | 标定含义 |
|---|---|---|
| 后天车上斜床XY到中心基准 | 否，默认0 | 中心位的基准AbsY；微调后握手内按长度/孔型移动到顶尖对中Y |
| 后天车从斜床下料取板 | 是，yOff | 基准转换为当块板动态取料AbsY，防止被微调拉回中心 |
| 研磨ST709取板、研磨机上/下料、ST710放板 | 否，默认0 | 直接使用天车5对应工位AbsY，不套斜床yOff |
| 前天车货叉/打号机/中转架 | 否，默认0 | 天车1/3对应站号标定 |

## 4. 为什么偏差2mm可能不发微调

规则为`|目标Abs-当前Abs| <= toleranceMm`时通过；超过容差但不超过maxAdjustMm才发修正。源码模型X/Y默认容差5mm，核对时工作区JSON也是5mm，因此**实际偏差2mm只通过核对，不会发送2mm微调**。

若参数已通过配置页改为容差1mm，偏差+2mm且在最大修正内，则天车4的Y显示目标增加2mm；天车2或5的Y显示目标减少2mm。X偏差+2mm则显示X增加2mm。X/Y都需要校正时同一次MoveAbsoluteAsync(targetX,targetY,-1)联合微调；合格轴传-1保持，不发Z下降。

以下是假设日志示例，不是实机采集（2号线天车4，ST606下料，基准AbsY=1000、yOff=100、显示Y=800、当前AbsY=898，且X偏差同为+2、容差已设1）：

```text
[YAbsFineTune] [2号线后天车-ST606下料取料前] ST606 需要同步微调(1/2):
  显示Y=800, 当前AbsY=898, 基准AbsY=1000,
  动态偏移=-100mm, 动态目标AbsY=900, Y方向=+1,
  ΔAbs=2mm, Δ显示=2mm → 微调显示Y=802
[XAbsFineTune] [2号线后天车-ST606下料取料前] ST606 需要同步微调(1/2):
  ΔAbs=2mm, Δ显示=2mm → 微调显示X=原显示X+2
绝对值微调命令已发送：XY联合微调，Z保持。
```

实际完整日志由helper输出，包括“微调前沉降”“状态采样/状态未稳定”“需要同步微调”“微调后共同沉降”“Abs反馈跟随”“最终复核”。最多两轮修正；输入窗口不稳定、偏差超最大、编码器不跟随或最终仍超差都阻止后续放/取板步骤，并进入所属动作异常流程。

## 5. 默认值与本次工作区快照

| 配置项 | C#模型默认 | 当前工作区JSON（2026-10-11） |
|---|---:|---:|
| safety.plateLengthToleranceMm | 15mm | 15mm |
| safety.pressureStopNormalPositionToleranceMm | 15mm | 15mm |
| X/Y微调toleranceMm | 5mm | 5mm |
| X/Y微调maxAdjustMm | 50mm | 100mm |
| absFineTuneVerification.beforeReadSettleDelayMs | 1200ms | 700ms |
| stableSampleCount/Interval/Range | 3次/200ms/1mm | 3次/200ms/1mm |
| afterMoveMinSettleDelayMs | 1200ms | 700ms |
| grinding.safeZHeight | 0 | 0 |
| grinding.x11StableDelayMs | 3000ms | 500ms |
| grinding.m731NotifyDelayMs/m721NotifyDelayMs | 1000ms/1000ms | 2000ms/2000ms |
| grinding.maxFifoCount | 2 | 13 |
| grinding.lineStartWaitTimeoutMs | 120000ms | 120000ms |
| grinding.loading/waitingForUnload/unloadingTimeoutMinutes | 10/20/10min | 10/60/10min |

以上区分模型默认与用户部署参数，未更改任何配置。JSON最大长度字典里“ST 701”等含空格键与实际“ST701”不同；当前准入按准确站号匹配，仅实际ST701～ST704的有效键参与，不靠空格别名。

## 6. 生效核对步骤

1. 看程序启动日志中的配置加载绝对路径，确认修改的是该运行目录文件。
2. 配置页编辑需完成绑定提交，核对天车号、站号、Abs方向、标定及容差；保存后核对正式JSON，不把仓库值当现场值。
3. 外部文件修改需重新加载/重启相应程序实例；仅Pause/Resume没有配置Reload。
4. 更改标定后核对下一次开始的微调日志基准Abs与动态Abs，不用上一趟缓存坐标做结论。
5. 偏差满足容差只记录通过；需要调整时先核对修正方向、幅度和稳定反馈。程序能发修正不证明标定本身正确，现场应先完成对应工位标定。

源码：Infrastructure/Config/MotionConfig.cs；Presentation/ViewModels/Config/ConfigPageViewModel.cs；HomeViewModel.CreateConfigPageViewModel；XAbsFineTuneHelper.ResolveActiveAxes/VerifyAndFineTuneAsync；两条后端DoHandshake/DoUnload；GrindingFlowEngine四处VerifyAndFineTuneAsync。
