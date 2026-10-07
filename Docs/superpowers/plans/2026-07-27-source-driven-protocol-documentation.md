# 源码事实优先的通信与设备交互文档更新 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 以当前源码为证据，补全并校正通信协议、设备交互流程、协议速查表和全线业务文档，且不改生产代码。

**Architecture:** 通信协议手册记录单设备的地址、数据类型、指令和确认；协议速查表只保留跨设备索引；03 流程文件记录交接顺序、缓存和闭环；全线业务文档说明派发、异常和人工处置边界。所有事实必须能追溯到服务、地址定义或流程引擎。

**Tech Stack:** Markdown、C# 源码静态审读、`rg`、Git 差异检查。

---

### Task 1: 建立文档—源码证据映射

**Files:**
- Modify: `Docs/README.md`
- Modify: `Docs/02-通信协议手册/通信矩阵总览.md`
- Inspect: `Communication/DeviceServices/*.cs`, `Communication/DeviceAddresses/*.cs`, `Communication/Clients/*.cs`, `Service/FlowEngine/*.cs`

- [ ] **Step 1: 列出每份协议和流程文档的章节及对应源码服务。**

  运行：
  ```powershell
  rg -n '^#|^##|^###' Docs\\02-通信协议手册 Docs\\03-设备交互流程
  rg --files Communication\\DeviceServices Communication\\DeviceAddresses Service\\FlowEngine
  ```

- [ ] **Step 2: 在通信矩阵总览中增加“源码依据与文档层级”表。**

  表中为每类设备列出服务、地址定义、主要流程引擎、协议详情和流程详情文档；注明地址和行为均以当前代码为准。

- [ ] **Step 3: 在 Docs README 中说明协议、流程和业务文档的职责边界。**

  明确“协议手册不代替 PLC 原始电气手册”“流程文档不把缓存当物理信号”“业务文档不把指令发送当完成确认”。

- [ ] **Step 4: 核对映射中的文件均存在。**

  运行：
  ```powershell
  rg --files Docs\\02-通信协议手册 Docs\\03-设备交互流程
  ```

### Task 2: 更新天车、机械手与共享互锁文档

**Files:**
- Modify: `Docs/02-通信协议手册/天车手动控制协议_完整寄存器.md`
- Modify: `Docs/02-通信协议手册/天车交互协议.md`
- Modify: `Docs/02-通信协议手册/天车机械手寄存器地址速查表.md`
- Modify: `Docs/03-设备交互流程/天车_调度逻辑.md`
- Modify: `Docs/03-设备交互流程/前后天车相邻碰撞区锁设计_ZoneMT_ZoneTS.md`
- Inspect: `Communication/DeviceServices/CraneService.cs`, `Communication/DeviceServices/ManipulatorConnectionCache.cs`, `Communication/DeviceAddresses/CraneAddress.cs`, `Service/FlowEngine/*FlowEngine.cs`

- [ ] **Step 1: 从 CraneService 和地址定义提取状态、目标、触发、磁铁、接液盘和 X2 的实际读写规则。**

  必须写清 DINT 小端目标坐标、D4520/4521/4522 的 `2→0` 脉冲、位置轮询的 ±5mm 默认容差、超时、取消和 finally 复位。

- [ ] **Step 2: 写明实际的 Z 方向顺序和下压边界。**

  Z下降为 XY 到位后下降；Z上升为先升 Z 后 XY；X2 仅由上位机在 Z 下降轮询期间检测。把恢复方法“不验证实际位置/不自动安全抬Z”的事实列为当前实现边界。

- [ ] **Step 3: 更新调度与锁文档。**

  说明服务自身不提供动作互斥、连接锁不等于运动锁；实际动作互斥由各引擎的位置锁、动作锁、ZoneMT/ZoneTS 和 finally 收尾保证。不得把软件锁释放表述为设备已经物理安全。

- [ ] **Step 4: 复核天车关键地址在三份协议文档与速查表一致。**

  运行：
  ```powershell
  rg -n 'D3102|D4520|D4521|D4522|D4523|D4518|X2' Docs\\02-通信协议手册 Docs\\全线设备协议寄存器速查总表.md
  ```

### Task 3: 更新 MC、货叉、双头镗与打标协议/流程

**Files:**
- Modify: `Docs/02-通信协议手册/货叉及辅助PLC协议_三菱MC.md`
- Modify: `Docs/02-通信协议手册/双头镗协议_Syntec.md`
- Modify: `Docs/02-通信协议手册/打标机协议_文件握手.md`
- Modify: `Docs/03-设备交互流程/货叉与双头镗交互流程.md`
- Modify: `Docs/03-设备交互流程/打标机_文件交互流程.md`
- Inspect: `Communication/DeviceServices/McConnectionCache.cs`, `ForkService.cs`, `BoringModbusService.cs`, marking services and `Line1FrontFlowEngine.cs`, `Line2FrontFlowEngine.cs`

- [ ] **Step 1: 记录 MC63/64/65 的连接、M/D 访问与按字单 bit 写入语义。**

  将“读-改-写需要串行保护”“写成功返回不天然等于 PLC 物理动作完成”写入协议层，并为 M800、M801、M817/M818/M821、M720/M721、M710/M711 标出读写方和软件缓存条件。

- [ ] **Step 2: 重写货叉—双头镗握手的状态顺序。**

  以 R6101→参数→R6102→R6103→M912→R6104→R6107→M913→R6108 为主线，说明跳镗 M914 分支、参数缩放和未请求数据时的等待而非业务超时。

- [ ] **Step 3: 补全打标文件交互的文件名、写入时机、读取/确认和失败保留行为。**

  不从文件存在推断已加工；将实际的重试、异常和后续工件交接边界写清。

- [ ] **Step 4: 交叉检查关键握手编号与流程顺序。**

  运行：
  ```powershell
  rg -n 'M800|M801|M817|M818|M820|M821|M710|M711|M720|M721|R610[1-8]|M912|M913|M914' Docs\\02-通信协议手册 Docs\\03-设备交互流程
  ```

### Task 4: 更新研磨机和两类斜床协议/交互流程

**Files:**
- Modify: `Docs/02-通信协议手册/研磨机协议_ModbusTCP.md`
- Modify: `Docs/02-通信协议手册/斜床协议/锦州斜床_ModbusTCP.md`
- Modify: `Docs/02-通信协议手册/斜床协议/沈阳斜床_FANUC_FOCAS.md`
- Modify: `Docs/03-设备交互流程/后天车与斜床交互流程.md`
- Inspect: `Communication/DeviceServices/PlcGrinderService.cs`, `ModbusSkewBedService.cs`, `FanucSkewBedService.cs`, relevant address files, `GrindingFlowEngine.cs`, `Line1RearFlowEngine.cs`, `Line2RearFlowEngine.cs`

- [ ] **Step 1: 补全 TypeA/TypeB 研磨机的输入、输出、参数、取料前二次确认和下料完成闭环。**

  说明磨石报警是单机禁派发，报警机不接新料、其他机继续；已在机内工件不因该信号中断下料。明确缓存 FIFO、ST709、ST701~704、ST710、M721 和研磨机确认的关系。

- [ ] **Step 2: 补全两类斜床的数据模型、读写、状态新鲜度和完成交接。**

  区分 Modbus 地址与 FANUC 宏变量；注明读回验证只在源码实际执行的参数写场景存在，不泛化到所有命令。

- [ ] **Step 3: 更新后天车与斜床流程。**

  写明中转架“物理有板+软件缓存”、斜床候选、X11、夹紧/松开、M817/M818/M821/M720 分流、M720 等待和异常后暂停/人工确认的实际边界。

- [ ] **Step 4: 复核研磨/斜床文档未把维护报警或命令发送写成加工/物理完成。**

  运行：
  ```powershell
  rg -n '磨石|报警|已完成|写入成功|物理|缓存|X11|M720|M721|M711' Docs\\02-通信协议手册 Docs\\03-设备交互流程
  ```

### Task 5: 汇总速查表和全线业务文档，完成一致性验证

**Files:**
- Modify: `Docs/全线设备协议寄存器速查总表.md`
- Modify: `Docs/全线业务逻辑详解.md`
- Modify: `Docs/03-设备交互流程/整线引擎流程总览_按代码.md`
- Inspect: all files changed in Tasks 1–4 and corresponding source files

- [ ] **Step 1: 将速查表改为每类设备的“地址/动作/完成确认/当前边界”索引。**

  详细的字段解释只在对应协议文档维护；速查表只保留定位所需的关键信息和链接，防止多处复制后漂移。

- [ ] **Step 2: 回写全线业务文档。**

  补充通信重试的实际层次、写命令响应未知、下压恢复不等于位置已安全、任务/缓存/PLC 信号的边界；不改变已有正确业务流程描述。

- [ ] **Step 3: 更新整线流程总览中的索引和交接终点。**

  让读者可以从总览跳转到设备协议、交互流程及主业务规则，不重复设备寄存器细节。

- [ ] **Step 4: 运行文档和差异验证。**

  运行：
  ```powershell
  git diff --check -- Docs
  rg -n 'TODO|TBD|待定|占位' Docs\\02-通信协议手册 Docs\\03-设备交互流程 Docs\\全线设备协议寄存器速查总表.md Docs\\全线业务逻辑详解.md
  git diff -- Docs
  ```

- [ ] **Step 5: 只提交本任务创建或修改的文档。**

  运行：
  ```powershell
  git add -- Docs
  git commit -m "docs: align communication and device flow manuals with source"
  ```
