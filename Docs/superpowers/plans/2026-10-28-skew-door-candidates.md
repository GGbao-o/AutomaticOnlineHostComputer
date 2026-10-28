# 斜床上料、下料候选增加门开条件：实施计划

**状态：** 2026-10-28用户批准后已实现并完成本地软件验证；设备类型和候选阶段范围按原确认方案执行。

**目标：** 1/2号线对应斜床门开信号原始值必须等于1，才可进入上料或下料候选。门未开、非1或本轮读取失败时跳过该床，继续检查其他可用床，后续轮询恢复后重新参与候选。

**架构：** 在现有单设备信号快照中增加独立`DoorOpen`字段，两线后端与原请求信号一起接收。上料通过现有`IsReadyForLoad`过滤，下料在保留状态同步和完工记录导出之后过滤；原拿到后天车锁后的候选重扫自然复用同一条件。不在候选循环中增加单独PLC读取或持锁等门。

**技术：** C#/.NET 8、WPF、锦州Modbus TCP、FANUC FOCAS专属Worker、xUnit。后续实现使用apply_patch和详细中文注释，不用Python更新源码。

## 1. 设备与协议映射

| 线路 | 设备 | 数量 | 协议 | 门开条件 |
|---|---|---:|---|---|
| 1号线 | ST108、ST109、ST110、ST111 | 4 | 锦州Modbus TCP | 10359原始Word == 1 |
| 1号线 | ST112 | 1 | FANUC FOCAS | #1006原始宏变量 == 1 |
| 2号线 | ST606、ST607、ST608、ST609、ST610 | 5 | FANUC FOCAS | #1006原始宏变量 == 1 |

`TailstockOpened`/现有`Opened`是尾座张开，与门开不同，不能复用。原其它信号的非0解释规则保持不变；只有新增门开字段严格要求1，不将2、-1或未知当开门完成。

## 2. 本次范围与行为

- 门未开是候选准入未满足，不是已分配动作严重异常；不新弹严重告警、不暂停线路、不清Wp/来源缓存/Pending、不写PLC、不执行开门。
- 只影响对应斜床；其他门开且条件齐全的斜床仍可参与调度。
- 保留中转架物理板、软件缓存、到达序号、人工冻结、版长能力、Active预约、锁和压力选择规则。用户前述普通Active预约问题不并入本次修改。
- 不修改DoLoad/DoUnload、取放料高度、握手、运动、充退磁、分流和严重异常收尾。
- 不增加“已经持板后在斜床前等门”、Z下降前再次读门或运行中持续门联锁。本次范围为候选及派发前候选重扫。
- 不新增JSON开关、时间阈值或配置页面项；门开是固定候选必要条件。

## 3. 文件与实现步骤

### 3.1 实现前记录和测试

- [x] 保存当前Git差异、相关文件哈希与Line1Rear原有手动注释，不覆盖用户正在编辑的配置及总文档。
- [x] 构建并执行当前测试基线，确认645项、636通过、9失败，并记录实际失败名称。
- [x] 新增`AutomaticOnlineHostComputer.Tests/FlowEngine/SkewDoorCandidateContractTests.cs`及`AutomaticOnlineHostComputer.Tests/Communication/SkewDoorSignalSnapshotTests.cs`，覆盖两种服务采样和两线候选过滤；未接入时12项候选测试失败。

测试目标：门开原值1允许；0/2/-1不允许；短报文/通信失败不沿用旧1；两线不同设备类型映射正确；上下料锁后重扫仍检查门；门未开不暂停、不清板、不启动动作；下料门关闭不妨碍WaitingUnload同步和job2导出。

### 3.2 锦州Modbus信号快照

修改`Communication/DeviceServices/ModbusSkewBedService.cs`：

- [x] `ReadSignalsAsync`从原10352～10358扩为10352～10359，保持一次FC03块读，读取长度从7变8，不另发逐台门开查询。
- [x] 返回值扩为六个具名字段`(rqData, rqLoad, clamped, rqUnload, opened, doorOpen)`；前五个顺序及非0含义保持原样。
- [x] 新字段使用10359的**原始值==1**。复用现有越界/短报文验证，缺10359抛读取异常使本轮失效，不能补0后假称读取成功。
- [x] 独立`IsDoorOpenAsync`同步按原始10359==1解释；未修改通用`ReadBoolRawAsync`。

接口及核心判断：

```csharp
public async Task<(bool rqData, bool rqLoad, bool clamped,
    bool rqUnload, bool opened, bool doorOpen)> ReadSignalsAsync(CancellationToken ct = default)
// 同一次读取：start=RequestData(10352)，end=DoorOpen(10359)。
// 原五字段保留现有解析；doorOpen = 该块中10359经长度校验后的原始Word == 1。
```

当前检索到该块快照的生产调用方仅Line1Rear.PollBed，使用具名字段，不是五元组解构；实施时重新全库检索并通过编译确认所有调用方。

### 3.3 FANUC Worker快照

修改`Communication/DeviceServices/FanucSkewBedService.cs`：

- [x] `FanucSignalSnapshot`增加默认false的`DoorOpen`属性，与尾座`Opened`独立。
- [x] `PollSnapshotOnce`在原#1001～#1005读取后读取地址常量`FanucSkewBedAddress.AutoDoorOpen`（#1006），读取间保留取消检查；全部成功后才发布同一份完整快照。
- [x] 新字段赋值`DoorOpen = doorOpenRaw == 1.0`，不复用通用非0解释。
- [x] 门读取失败仍进入既有Worker错误快照/断开退避流程，错误快照Connected=false、DoorOpen=false，不发布半新半旧快照。
- [x] 保持所有FOCAS读写都由该设备原Worker执行；后端主循环未直接调用SDK，未增加线程。
- [x] 显式`ReadSignalsAsync`补第六个doorOpen字段及严格解析，仍通过原SafeCallAsync排队执行。

```csharp
// 仅在原PollSnapshotOnce及其专属Worker线程中读取：
double doorOpenRaw = _sdk.GetMacro(FanucSkewBedAddress.AutoDoorOpen);
// 发布完整快照时增加：DoorOpen = doorOpenRaw == 1.0;
```

不改变原请求/夹紧/尾座张开宏变量的解析方式和FOCAS句柄线程归属。

### 3.4 两条线后端缓存及上料候选

修改`Service/FlowEngine/Line1RearFlowEngine.cs`、`Line2RearFlowEngine.cs`：

- [x] 两线各自`SkewCtx`增加独立`DoorOpen`字段，默认false。
- [x] `PollBed`在原信号读入和PollVersion校验保护内一起提交门开；1号线ST108～ST111取Modbus第六字段、ST112取FANUC快照；2号线五台全部取FANUC快照。
- [x] 本轮读失败、离线跳过、失效、清理/应急复位时门许可清false，与SignalFresh失效配套；未写门寄存器。
- [x] 上料统一入口变为：

```csharp
private static bool IsReadyForLoad(SkewCtx? b)
    => b != null && b.Idle && b.SignalFresh && b.RqData && b.DoorOpen;
```

- [x] GetReadyBedsByPriority及上料候选沿用原调用关系；某床关门就不加入可上料床列表，原版长匹配继续找其他床，不改变来源消费或预约时机。

### 3.5 下料候选及锁后复核

- [x] `BuildUnloadCandidateScanAsync`保留原Machining→WaitingUnload同步、正在下料统计、Wp检查和job2导出位置。
- [x] 在完工导出成功之后、目的地查询/机械手报警查询和Candidates.Add之前插入门开筛选：

```csharp
if (!b.DoorOpen)
{
    if (logDetails)
        Console.WriteLine($"│   [下料候选] {b.Code}门开未确认(要求门开原值=1)，跳过本轮");
    continue;
}
```

- [x] 门未开不把WaitingUnload改Idle，不清Wp，不清CompletionExported；job2仍按原语义只成功导出一次。
- [x] 保留锁内重新扫描候选的入口及原finally释放未派发动作后天车锁的行为，未新增绕过条件的派发路径。
- [x] 重扫依赖已提交的本轮信号快照，不额外读取PLC，不承诺捕获尚未进入快照的物理关门变化。

### 3.6 日志、文档及范围复核

- [x] 信号汇总和上料不可用原因增加“门开确认”字段；读失败/SignalFresh=false显示未知/失效。
- [x] 同步两种斜床协议、后天车与斜床交互流程及整线引擎总览，标明严格==1、候选阶段和设备映射。
- [x] 总文档7.4/7.5分别补充门开准入段落，原用户大段编辑保留，提交时只选择新增两段。
- [x] 新增专项运维说明并更新Docs/README；本计划状态同步为已实现并验证。

## 4. 验证、交付与提交

建议执行顺序：

```powershell
dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --verbosity quiet -p:WarningLevel=0 --filter 'FullyQualifiedName~SkewDoor|FullyQualifiedName~RearSkewDispatchPlannerTests|FullyQualifiedName~ManipulatorAlarmDispatchGateContractTests' -- RunConfiguration.TargetPlatform=x86
dotnet build AutomaticOnlineHostComputer.csproj --no-restore --verbosity quiet -p:WarningLevel=0
dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --verbosity quiet -p:WarningLevel=0 -- RunConfiguration.TargetPlatform=x86
git diff --check
```

- [x] 实际候选测试覆盖两线门关/恢复/另一床、六台FANUC内存快照；Modbus报文测试覆盖10359严格值及原其它信号语义，映射按原设备分支核对。
- [x] 实际下料候选测试验证WaitingUnload/Wp保留、job2一次导出；锁后重扫入口/原释放逻辑和来源消费时机通过源码差异及契约核对。
- [x] FANUC使用托管错误快照及Worker接入契约验证，不调用真实原生SDK；Modbus通过127.0.0.1模拟PLC验证完整/短报文。
- [x] 全量失败名称与基线逐项相同；未修复无关失败，源码变更限定四文件。
- 工作区原有空白和用户编辑保留，最终选择性暂存执行`git diff --cached --check`；只本地commit，不push。

## 5. 需要准确保留的限制

这是候选准入，不是持续物理门联锁；门在派发后的变化不属于本次检查范围。现有FANUC后端主要依据LatestSnapshot.Connected和SignalFresh接收快照，没有在该处按CapturedAt做年龄校验；本方案不擅自增加新的超时阈值或变更原轮询策略，不能将本轮快照称作任意时刻绝对实时的门状态。若后续要求拿锁后强制重读或Z下降前确认，需要单独明确等待、失败和已持件处置规则。

## 6. 本地执行结果

新增候选测试实现前12项失败，实现后通过；本轮新增24项测试，相关46项全部通过，主项目构建通过。全量669项、660通过、9失败；基线645项、636通过、9失败，失败名称逐项一致。TRX及原始用户差异记录在独立`.planning/2026-10-28-skew-door-candidates/`目录，不纳入提交。未连接现场PLC或执行真实FOCAS原生操作，设备联调仍未开展。现行说明见[专项运维文档](../../运维/2026-10-28斜床上下料候选门开准入.md)。
