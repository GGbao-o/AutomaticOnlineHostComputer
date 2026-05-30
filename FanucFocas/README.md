# FanucFocas

FANUC CNC 通信 SDK，`netstandard2.0`，兼容 .NET Framework 4.6.1+ / .NET Core 2.0+ / .NET 5+。

## 使用

### 连接 → 读写 → 断开

```csharp
using FanucFocas;

// 连接
FanucSdk.Connect("192.168.1.21");

// 读取
var pos    = FanucSdk.GetPosition();      // 位置
var status = FanucSdk.GetStatus();        // CNC 状态
double rpm = FanucSdk.GetSpindleSpeed();  // 主轴转速
double ld  = FanucSdk.GetSpindleLoad();   // 主轴负载 %
int prog   = FanucSdk.GetProgramNumber(); // 程序号
int feed   = FanucSdk.GetFeedOverride();  // 进给倍率
int sp     = FanucSdk.GetSpindleOverride();// 主轴倍率
int tool   = FanucSdk.GetToolNumber();    // 刀号
bool esp   = FanucSdk.GetEmergencyStop(); // 急停信号
var alarm  = FanucSdk.GetAlarm();         // 报警
var sys    = FanucSdk.GetSysInfo();       // 系统信息
int param  = FanucSdk.GetParameter(6711); // 参数

// 宏变量读写
double val = FanucSdk.GetMacro(1000);
FanucSdk.SetMacro(1100, 1.0);

// PMC 读写
byte[] b = FanucSdk.PmcReadByte(5, 12, 13);  // R12-R13
FanucSdk.PmcWriteByte(0, new byte[]{0x01});

// 断开
FanucSdk.Disconnect();
```

### 低级 API（直接调 FOCAS 函数）

```csharp
using static FanucFocas.Focas1;

ushort h = FanucSdk.Handle;
ODBST st = new ODBST();
cnc_statinfo(h, st);
```

## API 列表

| 方法 | 说明 |
|------|------|
| `Connect(ip, port, timeoutMs)` | 连接 CNC |
| `Disconnect()` | 断开 |
| `GetPosition()` | 读取第1轴位置 |
| `GetPositionRaw()` | 读取所有轴原始数据 |
| `GetStatus()` | CNC 运行状态 |
| `GetSpindleSpeed()` | 主轴转速 rpm |
| `GetFeedRate()` | 进给速度 |
| `GetSpindleLoad()` | 主轴负载 % |
| `GetProgramNumber()` | 当前程序号 |
| `GetSequenceNumber()` | 当前顺序号 |
| `GetAlarm()` | 最新报警 |
| `GetSysInfo()` | 系统信息 |
| `GetParameter(no)` | 读取参数 |
| `SetParameter(no, val)` | 写入参数 |
| `GetMacro(no)` | 读取宏变量 |
| `SetMacro(no, val)` | 写入宏变量 |
| `GetMacroRaw(no, ..)` | 读取宏变量原始值 |
| `PmcReadByte(t, s, e)` | 读 PMC 字节 |
| `PmcReadWord(t, s, e)` | 读 PMC 字 |
| `PmcWriteByte(s, d)` | 写 PMC 字节 |
| `PmcWriteWord(s, d)` | 写 PMC 字 |
| `GetFeedOverride()` | 进给倍率 % |
| `GetSpindleOverride()` | 主轴倍率 % |
| `GetEmergencyStop()` | 硬急停 X7.4 |
| `GetToolNumber()` | 当前刀号 D26 |

## DLL 系列匹配

如果连接报 `EW_SOCKET(-16)` 或 `EW_VERSION(-7)`，说明 `Fwlib32.dll` 与现场 CNC 系列不匹配。从 `dlls/` 目录找到对应系列 DLL，改名覆盖 `Fwlib32.dll`：

| CNC 系列 | DLL |
|----------|-----|
| 0i-A | `Fwlib0i.dll → Fwlib32.dll` |
| 0i-B | `Fwlib0iB.dll → Fwlib32.dll` |
| 0i-D | `Fwlib0iD.dll → Fwlib32.dll` |
| 0i-DN | `fwlib0DN.dll → Fwlib32.dll` |
| 30i | `fwlib30i.dll → Fwlib32.dll` |

## 错误处理

所有方法在 FOCAS 返回非 0 时抛出 `FocasException`，`ErrorCode` 属性包含原始错误码。
