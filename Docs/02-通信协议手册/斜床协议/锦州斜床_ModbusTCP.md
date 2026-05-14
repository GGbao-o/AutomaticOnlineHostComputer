# 锦州斜床通信地址（迅捷 PLC，Modbus TCP）

## 概述
- **设备数量**：4 台
- **协议**：Modbus TCP
- **端口**：502
- **单元 ID**：1
- **地址段**：保持寄存器 10300~10374，FC03 读 / FC06 写
- **代码定义**：`Communication/DeviceAddresses/ModbusSkewBedAddress.cs`

---

## 状态读取（FC03，INT16 单寄存器）

| Modbus 地址 | 常量名 | 功能 | 值说明 |
|------|------|------|------|
| 10350 | `MachineReady` | 准备就绪 | 0=未就绪，1=就绪 |
| 10351 | `OperationMode` | 操作模式 | 0=手动，1=半自动，2=全自动 |
| 10352 | `RequestData` | 请求数据 | 0=无请求，1=请求上位机下发参数 |
| 10354 | `RequestLoad` | 请求上料 | 0=无请求，1=请求天车送料 |
| 10355 | `TailstockClamped` | 尾座顶紧信号 | 0=未顶紧，1=已顶紧 |
| 10357 | `RequestUnload` | 请求下料 | 0=无请求，1=请求天车取料 |
| 10358 | `TailstockOpened` | 尾座张开到位 | 0=未张开，1=已张开 |
| 10359 | `DoorOpen` | 门开到位 | 0=门关，1=门已打开 |
| 10360 | `GrindHeadUpperLimit` | 磨头上限 | 0=未在上限，1=在上限位 |
| 10361 | `XAxisInPlace` | X 轴到位 | 0=未到位，1=在安全位置 |
| 10362 | `YAxisInPlace` | Y 轴到位 | 0=未到位，1=在安全位置 |
| 10363 | `Tool1LifeEnd` | 刀具 1 寿命到 | 0=正常，1=寿命到期 |
| 10364 | `Tool2LifeEnd` | 刀具 2 寿命到 | 0=正常，1=寿命到期 |

---

## 参数读取（FC03，FLOAT 双寄存器，IEEE 754 单精度，高字在前）

| Modbus 地址 | 常量名 | 功能 | 寄存器数 |
|------|------|------|:---:|
| 10300~10301 | `RollerLength` | 版长（mm） | 2 |
| 10302~10303 | `BorePlugSize` | 堵孔尺寸（mm） | 2 |
| 10304~10305 | `RollerDiameter` | 成活直径（mm） | 2 |
| 10394~10395 | `MachiningTime` | 加工时间（分钟） | 2 |

---

## 控制命令（FC06，INT16 单寄存器写）

| Modbus 地址 | 常量名 | 功能 | 值说明 |
|------|------|------|------|
| 10370 | `MachiningMode` | 加工模式 | 1=粗车，2=精车，3=研磨，4=粗精磨，5=精磨，6=粗精研磨 |
| 10371 | `TailstockClampCmd` | 远程尾座顶紧 | 1=顶紧指令 |
| 10372 | `RemoteStart` | 远程启动 | 1=启动指令 |
| 10373 | `TailstockOpenCmd` | 远程尾座张开 | 1=张开指令 |
| 10374 | `TailstockStopCmd` | 尾座停止 | 1=停止（天车异常时使用） |
