namespace AutomaticOnlineHostComputer.Communication.Models
{
    /// <summary>
    /// 通用读取结果，封装整数数组与浮点数组，
    /// 不同协议返回的数据类型不同，通过此类统一承载。
    /// </summary>
    public sealed class ReadResult
    {
        /// <summary>整数值数组（Modbus / 三菱MC / FOCAS 整型寄存器）。</summary>
        public int[] IntValues { get; }

        /// <summary>浮点值数组（FANUC 宏变量 / Syntec 宏变量）。</summary>
        public double[] DoubleValues { get; }

        /// <summary>
        /// 是否以浮点方式存储结果。
        /// true  → 优先使用 <see cref="DoubleValues"/>；
        /// false → 优先使用 <see cref="IntValues"/>。
        /// </summary>
        public bool IsDouble { get; }

        /// <param name="values">整数数组</param>
        public ReadResult(int[] values)
        {
            IntValues    = values;
            DoubleValues = Array.ConvertAll(values, v => (double)v);
            IsDouble     = false;
        }

        /// <param name="values">浮点数组</param>
        public ReadResult(double[] values)
        {
            DoubleValues = values;
            IntValues    = Array.ConvertAll(values, v => (int)v);
            IsDouble     = true;
        }

        // ── 便捷访问 ────────────────────────────────────────────────────

        /// <summary>获取第 0 个值（整数）。</summary>
        public int FirstInt => IntValues.Length > 0 ? IntValues[0] : 0;

        /// <summary>获取第 0 个值（浮点）。</summary>
        public double FirstDouble => DoubleValues.Length > 0 ? DoubleValues[0] : 0.0;
    }
}
