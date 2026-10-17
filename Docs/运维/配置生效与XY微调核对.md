# 配置生效与斜床/研磨XY微调核对

> 核对日期：2026-10-11。以MotionConfig、ConfigPageViewModel、XAbsFineTuneHelper及各引擎调用为依据。当前工作区JSON数值是核对时快照，不能冒充所有部署机的运行值。

## 1. 更改配置何时生效

| 更改方式 | 运行中内存 | 保存/下一次加载 |
|---|---|---|
| 在配置页编辑且绑定已提交到ViewModel/行模型 | 修改共享MotionConfig，后续读取可见，线路运行时也一样 | Save持久化；未保存值重启丢失 |
| 外部编辑程序运行目录Config/motion_settings.json | 不自动生效：没有文件监视或Reload入口 | 下次HomeViewModel加载时读取 |
| 外部编辑仓库Config/motion_settings.json | 不自动生效，也不保证是实际运行文件 | 构建/复制发布后还要核实运行目录文件与重新加载 |
| 改数据库工位坐标/偏移 | 不等同于修改绝对标定配置；各引擎有已加载缓存或初始化局部字段 | 以各具体重新加载/初始化入口为准，不能宣称所有在途动作立即改变 |
| 改数据库machine.ip/port | 设备管理保存只更新数据库和列表；已初始化生产上下文不自动切换 | 首次初始化或允许的显式重载重新装载；自动重连不等于重新查数据库 |

HomeViewModel构造调用MotionConfig.Load；CreateConfigPageViewModel把同一_cfg传给配置页，各引擎也共享该对象。Load基于AppDomain.CurrentDomain.BaseDirectory，不按当前PowerShell目录查找。Save序列化共享内存到同目录临时文件，验证JSON后替换正式文件并保留.bak；Save不是“重新读取外部刚改的文件”。

### 1.1 共享区X退避距离的页面入口（2026-10-12）

配置页“斜床参数”新增“共享区X退避距离(mm)”，分别编辑1号线1号斜床ST108和2号线6号斜床ST606，对应`skewBed.sharedAreaRetreatX.ST108/ST606`。页面读取当前字典，当前仓库两项均1000mm；不把模型ST108默认2000mm强行覆盖到页面。

绑定提交后直接更新共享内存，后续实际退避时由原后端读取；已经发出的退避目标不改变。点击保存沿用现有MotionConfig.Save持久化整个配置，未保存编辑重启丢失，外部编辑JSON不热加载。填写正整数；原后端在距离<=0时返回退避失败，不把0作为本轮新增的“禁用”开关。目标仍为实时当前X+配置距离，不叠加数据库偏移，上下料退避条件及异常收尾不变。

此次只增加ConfigPageViewModel两个属性和ConfigPageView两个输入，不修改模型默认、JSON现值或FlowEngine业务代码。主项目编译、XAML结构及字段映射核对通过，未连接设备或启动真实界面联调。

“即时可见”不表示已发送运动目标会改变。主移动坐标、safeZ、速度等一旦读入局部变量或发往PLC，本次已发送命令不撤销；下一个读取点才可能见新值。一次微调开始时固定轴目标、方向、容差/最大修正和共享验证参数；后续某些服务超时/其它动作参数仍在实际使用点读取，整趟搬运不是所有配置的原子快照。

### 1.2 前后天车X放行阈值（2026-10-14）

配置页“其他→前后天车X放行条件(mm)”增加四项：`cranePeerXAdmission.crane1MinCrane2X=-14000`、`crane2MaxCrane1X=2500`、`crane3MinCrane4X=-14000`、`crane4MaxCrane3X=2500`。1/3号前天车要求对方实际X大于等于各自阈值，2/4号后天车上料要求对方实际X小于等于各自阈值。四项独立、包含等号，负数和0有效，不叠加坐标偏移。

与一次取板的探测配置快照不同，这四个阈值每轮轮询重新读取。页面编辑提交后，volatile字段供引擎线程读取，可影响当前持锁等待的下一轮判断。原保存按钮持久化，外部JSON修改仍不热加载。旧配置缺节/缺字段使用上述默认值。等待不释放区域锁，无业务超时；取消及通信失败交原动作收尾。完整入口、人工移车示例及范围见[前后天车X持锁准入](2026-10-14前后天车X持锁准入.md)。

### 1.3 设备IP修改与自动重连（2026-10-17）

恢复原目标地址的连接通常不需要重启；“后来连接成功”不能单独证明已读取数据库新IP。设备管理保存后只刷新列表，切回已初始化主页面的默认LoadAsync也只是轻量刷新。代码保留受保护的forceReload=true重载能力，但当前未找到界面调用该入口；普通暂停/启动不能替代地址重载。天车、机械手客户端保存创建时的IP，研磨采集循环也保存初始化传入的IP。

研磨工件显示屏`grinding.workpieceDisplayIp`是不同入口：配置页修改共享内存，后续显示屏写入读取该配置；不能套用到数据库machine.ip。具体设备分类、连接恢复情景、源码依据和日志核对见[设备IP修改与自动重连](2026-10-17设备IP修改与自动重连说明.md)。

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

## 新取板动作的配置快照

新统一取板在每次动作开始时快照grinding.x11StableDelayMs（当前仓库500ms），同一趟各探测位置与充磁后确认使用同一个值，动作期间改共享内存不会中途改变它。前天车打号取回也使用本趟快照。外部JSON仍不自动热加载。

2026-10-12新增全线`grinding.pickupProbeCount`（默认3）及`grinding.pickupProbeStepMm`（默认5mm），配置页在全线X11稳定等待旁编辑，页面显示最大额外下降量=(次数-1)×步长。各次取板开始固定这两项，运行中的该次探测不变，后续取板使用当前共享内存值；前天车在打号机取回是新一次取板。保存持久化，外部JSON仍不热加载。两项必须为正整数、累计距离不得溢出，非法配置拒绝取料并走所属动作异常收尾。

首次Z仍由原直径/数据库/偏移公式计算，补偿以实际停稳Z0为基准，按配置步长递增（默认+5/+10），不重复套Z偏移。局部到位容差最大1mm，步长1mm时为0mm，运动超时仍10秒；不更改正常运动/XY绝对微调容差。斜床动态绝对Y及研磨Y标定原公式保持。见[统一取板说明](X11充磁前探测与异常作废.md)。
