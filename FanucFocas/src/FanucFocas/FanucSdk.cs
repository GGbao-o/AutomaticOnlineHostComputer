using System;
using static FanucFocas.Focas1;

namespace FanucFocas
{
    /// <summary>
    /// FANUC CNC 通信 SDK — 实例化使用, 每台CNC独立连接互不阻塞
    /// </summary>
    public class FanucSdk
    {
        private ushort _hndl;
        private readonly object _lock = new();
        private bool _connected;

        #region 连接 / 断开

        /// <summary>
        /// 连接 CNC。
        /// 注意：FOCAS cnc_allclibhndl3 的 timeout 参数单位是“秒”，不是毫秒。
        /// 调用方应直接传秒数，避免离线设备把预期 3 秒误放大成 30 秒。
        /// </summary>
        public bool Connect(string ip, ushort port = 8193, int timeoutSeconds = 3)
        {
            lock (_lock)
            {
                if (_connected) return true;
                int seconds = Math.Max(1, timeoutSeconds);
                short ret = cnc_allclibhndl3(ip, port, seconds, out _hndl);
                _connected = (ret == EW_OK);
                return _connected;
            }
        }

        /// <summary>断开</summary>
        public void Disconnect()
        {
            lock (_lock)
            {
                if (!_connected) return;
                cnc_freelibhndl(_hndl);
                _connected = false;
            }
        }

        public bool IsConnected { get { lock (_lock) return _connected; } }

        /// <summary>原始句柄（直接调 Focas1 函数时用）</summary>
        public ushort Handle
        {
            get { lock (_lock) { if (!_connected) throw new InvalidOperationException("未连接"); return _hndl; } }
        }

        #endregion

        #region 位置

        /// <summary>读取第1轴位置</summary>
        public PositionResult GetPosition()
        {
            Call(h =>
            {
                var pos = new ODBPOS();
                short num = MAX_AXIS;
                Check(cnc_rdposition(h, -1, ref num, pos));
                return pos;
            }, out var p);

            return new PositionResult
            {
                Absolute = Decode(p.p1.abs),
                Machine  = Decode(p.p1.mach),
                Relative = Decode(p.p1.rel),
                Distance = Decode(p.p1.dist),
            };
        }

        /// <summary>读取所有轴原始位置数据</summary>
        public ODBPOS GetPositionRaw()
        {
            Call(h =>
            {
                var pos = new ODBPOS();
                short num = MAX_AXIS;
                Check(cnc_rdposition(h, -1, ref num, pos));
                return pos;
            }, out var p);
            return p;
        }

        #endregion

        #region 状态

        public CncStatus GetStatus()
        {
            Call(h => { var s = new ODBST(); Check(cnc_statinfo(h, s)); return s; }, out var s);
            return new CncStatus
            {
                AutoMode  = s.aut, RunStatus = s.run, Motion = s.motion,
                Emergency = s.emergency == 1, Alarm = s.alarm == 1,
                Mstb = s.mstb, TMMode = s.tmmode,
            };
        }

        public short GetOperationMode()
        {
            Call(h => { short m; Check(cnc_rdopmode(h, out m)); return m; }, out var v);
            return v;
        }

        #endregion

        #region 速度 / 负载

        public double GetSpindleSpeed()
        {
            Call(h => { var a = new ODBACT(); Check(cnc_acts(h, a)); return (double)a.data; }, out var v);
            return v;
        }

        public double GetFeedRate()
        {
            Call(h => { var a = new ODBACT(); Check(cnc_actf(h, a)); return (double)a.data; }, out var v);
            return v;
        }

        public double GetSpindleLoad()
        {
            Call(h => { var s = new ODBSPN(); Check(cnc_rdspload(h, ALL_SPINDLES, s)); return s.data[0] / 200.0; }, out var v);
            return v;
        }

        #endregion

        #region 程序

        public int GetProgramNumber()
        {
            Call(h => { var p = new ODBPRO(); Check(cnc_rdprgnum(h, p)); return (int)p.data; }, out var v);
            return v;
        }

        #endregion

        #region PMC

        /// <summary>读 PMC 字节  addrType: 0=G 1=F 2=Y 3=X 4=A 5=R 6=T 7=K 8=C 9=D</summary>
        public byte[] PmcReadByte(short addrType, ushort start, ushort end)
        {
            Call(h =>
            {
                var p = new IODBPMC0();
                ushort len = (ushort)((end - start + 1) * 1 + 10);
                Check(pmc_rdpmcrng(h, addrType, 0, start, end, len, p));
                byte[] buf = new byte[end - start + 1];
                Array.Copy(p.cdata, buf, buf.Length);
                return buf;
            }, out var v);
            return v;
        }

        /// <summary>读 PMC 字 (16bit)</summary>
        public short[] PmcReadWord(short addrType, ushort start, ushort end)
        {
            Call(h =>
            {
                var p = new IODBPMC1();
                ushort len = (ushort)((end - start + 1) * 2 + 10);
                Check(pmc_rdpmcrng(h, addrType, 1, start, end, len, p));
                short[] buf = new short[end - start + 1];
                Array.Copy(p.idata, buf, buf.Length);
                return buf;
            }, out var v);
            return v;
        }

        /// <summary>写 PMC 字节。addrType: 0=G 1=F 2=Y 3=X 4=A 5=R 6=T 7=K 8=C 9=D</summary>
        public void PmcWriteByte(short addrType, ushort start, byte[] data)
        {
            Call(h =>
            {
                var p = new IODBPMC0();
                p.type_a = addrType; p.type_d = 0;
                p.datano_s = (short)start; p.datano_e = (short)(start + data.Length - 1);
                ushort len = (ushort)(10 + data.Length);
                Array.Copy(data, p.cdata, data.Length);
                Check(pmc_wrpmcrng(h, len, p));
                return 0;
            }, out _);
        }

        /// <summary>写 PMC 字</summary>
        public void PmcWriteWord(ushort start, short[] data)
        {
            Call(h =>
            {
                var p = new IODBPMC1();
                ushort len = (ushort)(10 + data.Length * 2);
                Array.Copy(data, p.idata, data.Length);
                Check(pmc_wrpmcrng(h, len, p));
                return 0;
            }, out _);
        }

        // 常用 PMC 封装
        public int  GetFeedOverride()    { var d = PmcReadWord(5, 12, 13); return d.Length > 0 ? d[0] : 0; }
        public int  GetSpindleOverride() { var d = PmcReadWord(5, 30, 31); return d.Length > 0 ? d[0] : 0; }
        public bool GetEmergencyStop()   { var d = PmcReadByte(3, 7, 12);  return d.Length > 1 && (d[1] & 0x10) != 0; }
        public int  GetToolNumber()      { var d = PmcReadByte(9, 26, 26); return d.Length > 0 ? d[0] : -1; }

        #endregion

        #region 参数

        public int GetParameter(int paramNo, short axisNo = -1)
        {
            Call(h => { var p = new IODBPSD_1(); Check(cnc_rdparam(h, (short)paramNo, axisNo, 4, p)); return p.ldata; }, out var v);
            return v;
        }

        public void SetParameter(int paramNo, int value)
        {
            Call(h => { var p = new IODBPSD_1 { datano = (short)paramNo, ldata = value }; Check(cnc_wrparam(h, 1, p)); return 0; }, out _);
        }

        #endregion

        #region 宏变量

        public double GetMacro(int number)
        {
            Call(h => { var m = new ODBM(); Check(cnc_rdmacro(h, (short)number, 10, m)); return m.mcr_val * Math.Pow(10, -m.dec_val); }, out var v);
            return v;
        }

        public void SetMacro(int number, double value)
        {
            int dec = GetDecPlaces(value);
            int mcr = (int)(value * Math.Pow(10, dec));
            Call(h => { Check(cnc_wrmacro(h, (short)number, 10, mcr, (short)dec)); return 0; }, out _);
        }

        public bool GetMacroRaw(int number, out int mcr_val, out short dec_val)
        {
            try
            {
                Call(h => { var m = new ODBM(); Check(cnc_rdmacro(h, (short)number, 10, m)); return (m.mcr_val, m.dec_val); }, out var t);
                mcr_val = t.mcr_val; dec_val = t.dec_val;
                return true;
            }
            catch { mcr_val = 0; dec_val = 0; return false; }
        }

        #endregion

        #region 报警

        public AlarmInfo GetAlarm()
        {
            try
            {
                Call(h =>
                {
                    ushort cnt; Check(cnc_rdalmhisno(h, out cnt));
                    if (cnt == 0) return new AlarmInfo { HasAlarm = false };
                    var his = new ODBAHIS();
                    Check(cnc_rdalmhistry(h, cnt, cnt, (ushort)(6 + 48), his));
                    var d = his.alm_his.data1;
                    return new AlarmInfo { HasAlarm = true, Number = d.alm_no, Message = d.alm_msg, Time = new DateTime(d.year + 2000, d.month, d.day, d.hour, d.minute, d.second) };
                }, out var r);
                return r;
            }
            catch { return new AlarmInfo { HasAlarm = false }; }
        }

        #endregion

        #region 系统信息

        public SysInfo GetSysInfo()
        {
            Call(h =>
            {
                var s = new ODBSYS(); Check(cnc_sysinfo(h, s));
                return new SysInfo { MaxAxis = s.max_axis, CncType = (short)s.cnc_type[0], MachineType = new string(s.mt_type).TrimEnd('\0'), Series = new string(s.series).TrimEnd('\0'), Version = new string(s.version).TrimEnd('\0'), Axes = new string(s.axes).TrimEnd('\0') };
            }, out var v);
            return v;
        }

        #endregion

        #region internal

        private void Call<T>(Func<ushort, T> fn, out T result)
        {
            lock (_lock)
            {
                if (!_connected) throw new InvalidOperationException("未连接，先调用 FanucSdk.Connect()");
                result = fn(_hndl);
            }
        }

        private void Check(short ret)
        {
            if (ret != EW_OK) throw new FocasException(ret);
        }

        private double Decode(POSELM e) => e.data * Math.Pow(10, -e.dec);

        private int GetDecPlaces(double n)
        {
            string s = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int i = s.IndexOf('.');
            return i == -1 ? 0 : s.Length - i - 1;
        }

        #endregion
    }

    #region 返回类型

    public class PositionResult
    {
        public double Absolute { get; set; }
        public double Machine  { get; set; }
        public double Relative { get; set; }
        public double Distance { get; set; }
        public override string ToString() => $"Abs={Absolute:F3} Mach={Machine:F3} Rel={Relative:F3} Dist={Distance:F3}";
    }

    public class CncStatus
    {
        public short AutoMode  { get; set; }
        public short RunStatus { get; set; }
        public short Motion    { get; set; }
        public short Mstb      { get; set; }
        public short TMMode    { get; set; }
        public bool  Emergency { get; set; }
        public bool  Alarm     { get; set; }

        public string RunStatusText => RunStatus switch { 0 => "复位", 1 => "停止", 2 => "暂停", 3 => "运行中", _ => RunStatus.ToString() };
        public string MotionText    => Motion    switch { 0 => "静止", 1 => "运动中", 2 => "驻留", _ => Motion.ToString() };
        public string AutoModeText  => AutoMode  switch { 0 => "MDI", 1 => "自动", 3 => "编辑", 4 => "手轮", 5 => "JOG", 9 => "回零", _ => AutoMode.ToString() };
        public override string ToString() => $"{RunStatusText} | {MotionText} | 急停={Emergency} | 报警={Alarm}";
    }

    public class AlarmInfo
    {
        public bool     HasAlarm { get; set; }
        public short    Number   { get; set; }
        public string   Message  { get; set; }
        public DateTime Time     { get; set; }
        public override string ToString() => HasAlarm ? $"AL{Number}: {Message} [{Time:yyyy/MM/dd HH:mm:ss}]" : "无报警";
    }

    public class SysInfo
    {
        public short  MaxAxis     { get; set; }
        public short  CncType     { get; set; }
        public string MachineType { get; set; }
        public string Series      { get; set; }
        public string Version     { get; set; }
        public string Axes        { get; set; }
        public string CncTypeName => CncType switch { 1 => "160", 2 => "150", 3 => "PowerMate", 4 => "PowerMate i", 5 => "160i-W", 6 => "150i", 7 => "0i-A", 8 => "0i-B", 9 => "300i", _ => $"Unknown({CncType})" };
        public override string ToString() => $"{CncTypeName} | {MachineType} | Series:{Series} Ver:{Version}";
    }

    public class FocasException : Exception
    {
        public short ErrorCode { get; }
        public FocasException(short code) : base(code switch
        {
            0 => "EW_OK", -1 => "EW_BUSY", -2 => "EW_RESET", -5 => "EW_SYSTEM", -6 => "EW_UNEXP",
            -7 => "EW_VERSION(DLL不匹配)", -15 => "EW_NODLL", -16 => "EW_SOCKET(网络不通)", -17 => "EW_PROTOCOL",
            -8 => "EW_HANDLE(FOCAS句柄无效)",
            1 => "EW_FUNC", 2 => "EW_LENGTH", 5 => "EW_DATA", 7 => "EW_PROT", 12 => "EW_MODE", 15 => "EW_ALARM", 16 => "EW_STOP",
            _ => $"错误码={code}"
        }) { ErrorCode = code; }
    }

    #endregion
}
