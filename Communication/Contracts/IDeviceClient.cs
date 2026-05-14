using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Models;

namespace AutomaticOnlineHostComputer.Communication.Contracts
{
    /// <summary>
    /// 通用设备通信客户端接口。
    /// 所有协议（Modbus TCP / 三菱MC / FANUC FOCAS / Syntec / 文件握手）均实现此接口，
    /// 上层业务代码无需关心底层协议差异。
    /// </summary>
    public interface IDeviceClient : IAsyncDisposable
    {
        // ─────────────────────────────────────────────────────────────────
        // 连接管理
        // ─────────────────────────────────────────────────────────────────

        /// <summary>获取当前是否已连接。</summary>
        bool IsConnected { get; }

        /// <summary>
        /// 异步建立连接。
        /// </summary>
        /// <param name="ct">取消令牌</param>
        Task ConnectAsync(CancellationToken ct = default);

        /// <summary>
        /// 断开连接并释放资源。
        /// </summary>
        Task DisconnectAsync();

        // ─────────────────────────────────────────────────────────────────
        // 读操作
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// 读取一组连续寄存器，返回 <see cref="ReadResult"/>（支持整数 / 浮点）。
        /// </summary>
        /// <param name="address">起始地址或变量号。含义由具体协议决定。</param>
        /// <param name="count">读取数量（字/寄存器个数）。</param>
        /// <param name="ct">取消令牌</param>
        Task<ReadResult> ReadAsync(int address, int count = 1, CancellationToken ct = default);

        /// <summary>
        /// 读取单个寄存器/变量，以双精度浮点返回。
        /// </summary>
        Task<double> ReadDoubleAsync(int address, CancellationToken ct = default);

        /// <summary>
        /// 读取单个寄存器/变量，以整数返回。
        /// </summary>
        Task<int> ReadIntAsync(int address, CancellationToken ct = default);

        // ─────────────────────────────────────────────────────────────────
        // 写操作
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// 向指定地址写入一个整数值。
        /// </summary>
        Task WriteAsync(int address, int value, CancellationToken ct = default);

        /// <summary>
        /// 向指定地址写入一个浮点值（对于只支持整型的协议，会自动取整或按比例缩放）。
        /// </summary>
        Task WriteAsync(int address, double value, CancellationToken ct = default);

        /// <summary>
        /// 批量写入多个地址的整数值。keys 和 values 长度必须一致。
        /// </summary>
        Task WriteBatchAsync(int[] addresses, int[] values, CancellationToken ct = default);

        /// <summary>
        /// 批量写入多个地址的浮点值。keys 和 values 长度必须一致。
        /// </summary>
        Task WriteBatchAsync(int[] addresses, double[] values, CancellationToken ct = default);
    }
}
