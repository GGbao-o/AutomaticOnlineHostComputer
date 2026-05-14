using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Contracts;
using AutomaticOnlineHostComputer.Communication.Models;

namespace AutomaticOnlineHostComputer.Communication.Clients
{
    /// <summary>
    /// 基于文件交换的握手协议通信客户端。
    /// <para>
    /// 适用设备：打号机 / 打标机（通过共享文件夹或本地目录交换指令和状态文件）。
    /// </para>
    /// <para>
    /// 协议说明（文件握手）：
    ///   1. 上位机将指令写入"指令文件"（如 cmd.txt），内容为 key=value 键值对。
    ///   2. 下位机轮询发现指令文件后执行，执行完成后写"完成文件"（如 ack.txt）。
    ///   3. 上位机检测到完成文件后读取状态，然后删除两个文件，完成一次握手。
    ///   4. 错误时下位机写"错误文件"（如 err.txt）。
    /// </para>
    /// <para>
    /// 目录结构示例：
    /// <code>
    ///   SharedDir/
    ///     cmd.txt   ← 上位机写，下位机读
    ///     ack.txt   ← 下位机写，上位机读（完成信号）
    ///     err.txt   ← 下位机写，上位机读（错误信号）
    /// </code>
    /// </para>
    /// <para>
    /// address 含义映射（可根据实际文档调整）：
    ///   address=1  → 工件编号（WorkpieceNo）
    ///   address=2  → 打标内容（MarkContent）
    ///   address=3  → 完成状态读取（读 ack.txt 中 status 字段）
    /// </para>
    /// </summary>
    public sealed class FileHandshakeClient : IDeviceClient
    {
        // ── 配置 ────────────────────────────────────────────────────────

        /// <summary>共享目录路径（本地路径或 UNC 路径，如 \\192.168.1.50\share）。</summary>
        private readonly string _sharedDir;

        private readonly string _cmdFile;  // 指令文件名
        private readonly string _ackFile;  // 完成确认文件名
        private readonly string _errFile;  // 错误文件名

        /// <summary>等待 ack/err 文件的轮询间隔（毫秒）。</summary>
        private readonly int _pollIntervalMs;

        /// <summary>等待 ack 文件的最大超时（毫秒）。</summary>
        private readonly int _ackTimeoutMs;

        private bool _isConnected;

        // ── 构造 ─────────────────────────────────────────────────────

        /// <param name="sharedDir">共享目录路径</param>
        /// <param name="cmdFile">指令文件名，默认 "cmd.txt"</param>
        /// <param name="ackFile">完成文件名，默认 "ack.txt"</param>
        /// <param name="errFile">错误文件名，默认 "err.txt"</param>
        /// <param name="pollIntervalMs">轮询间隔，默认 200ms</param>
        /// <param name="ackTimeoutMs">最大等待时间，默认 30000ms（30秒）</param>
        public FileHandshakeClient(
            string sharedDir,
            string cmdFile        = "cmd.txt",
            string ackFile        = "ack.txt",
            string errFile        = "err.txt",
            int    pollIntervalMs = 200,
            int    ackTimeoutMs   = 30_000)
        {
            _sharedDir     = sharedDir;
            _cmdFile       = cmdFile;
            _ackFile       = ackFile;
            _errFile       = errFile;
            _pollIntervalMs = pollIntervalMs;
            _ackTimeoutMs  = ackTimeoutMs;
        }

        // ── IDeviceClient ────────────────────────────────────────────

        /// <inheritdoc/>
        public bool IsConnected => _isConnected;

        /// <inheritdoc/>
        public Task ConnectAsync(CancellationToken ct = default)
        {
            // 检查共享目录是否可访问
            if (!Directory.Exists(_sharedDir))
                throw new DirectoryNotFoundException($"文件握手协议：共享目录不存在：{_sharedDir}");
            _isConnected = true;
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task DisconnectAsync()
        {
            _isConnected = false;
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// 读取完成状态文件（ack.txt）中对应 address 的值。
        /// address=3 表示读取 "status" 字段，返回 1=完成，0=未完成。
        /// </remarks>
        public async Task<ReadResult> ReadAsync(int address, int count = 1, CancellationToken ct = default)
        {
            EnsureConnected();
            var ackPath = Path.Combine(_sharedDir, _ackFile);
            if (!File.Exists(ackPath))
                return new ReadResult(new int[] { 0 }); // 尚无完成文件，返回 0

            var content = await File.ReadAllTextAsync(ackPath, Encoding.UTF8, ct);
            // 解析 key=value 格式，根据 address 取对应字段
            string key = AddressToKey(address);
            var val = ParseKv(content, key);
            if (double.TryParse(val, out double dval))
                return new ReadResult(new double[] { dval });
            return new ReadResult(new int[] { string.IsNullOrEmpty(val) ? 0 : 1 });
        }

        /// <inheritdoc/>
        public async Task<double> ReadDoubleAsync(int address, CancellationToken ct = default)
        {
            var r = await ReadAsync(address, 1, ct);
            return r.FirstDouble;
        }

        /// <inheritdoc/>
        public async Task<int> ReadIntAsync(int address, CancellationToken ct = default)
        {
            var r = await ReadAsync(address, 1, ct);
            return r.FirstInt;
        }

        /// <inheritdoc/>
        public async Task WriteAsync(int address, int value, CancellationToken ct = default)
        {
            await WriteAsync(address, (double)value, ct);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// 将 address 对应的 key=value 写入 cmd.txt 文件。
        /// 写完后等待 ack.txt 出现以确认下位机已接收。
        /// </remarks>
        public async Task WriteAsync(int address, double value, CancellationToken ct = default)
        {
            EnsureConnected();
            string key  = AddressToKey(address);
            string line = $"{key}={value}\n";
            var cmdPath = Path.Combine(_sharedDir, _cmdFile);

            // 追加或创建指令文件
            await File.AppendAllTextAsync(cmdPath, line, Encoding.UTF8, ct);
        }

        /// <inheritdoc/>
        public async Task WriteBatchAsync(int[] addresses, int[] values, CancellationToken ct = default)
        {
            if (addresses.Length != values.Length)
                throw new ArgumentException("addresses 与 values 数组长度不一致");
            var dbl = Array.ConvertAll(values, v => (double)v);
            await WriteBatchAsync(addresses, dbl, ct);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// 将所有 key=value 批量写入 cmd.txt，然后等待 ack.txt 确认。
        /// </remarks>
        public async Task WriteBatchAsync(int[] addresses, double[] values, CancellationToken ct = default)
        {
            if (addresses.Length != values.Length)
                throw new ArgumentException("addresses 与 values 数组长度不一致");
            EnsureConnected();

            // ── 1. 清除旧的 ack / err 文件 ────────────────────────────
            var ackPath = Path.Combine(_sharedDir, _ackFile);
            var errPath = Path.Combine(_sharedDir, _errFile);
            var cmdPath = Path.Combine(_sharedDir, _cmdFile);
            if (File.Exists(ackPath)) File.Delete(ackPath);
            if (File.Exists(errPath)) File.Delete(errPath);

            // ── 2. 写入指令文件 ───────────────────────────────────────
            var sb = new StringBuilder();
            for (int i = 0; i < addresses.Length; i++)
                sb.AppendLine($"{AddressToKey(addresses[i])}={values[i]}");
            await File.WriteAllTextAsync(cmdPath, sb.ToString(), Encoding.UTF8, ct);

            // ── 3. 等待 ack 或 err 文件出现 ───────────────────────────
            var deadline = DateTime.UtcNow.AddMilliseconds(_ackTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (File.Exists(errPath))
                {
                    string errMsg = await File.ReadAllTextAsync(errPath, Encoding.UTF8, ct);
                    File.Delete(errPath);
                    File.Delete(cmdPath);
                    throw new IOException($"文件握手协议：下位机报告错误：{errMsg}");
                }

                if (File.Exists(ackPath))
                {
                    // 握手完成，清理文件
                    File.Delete(ackPath);
                    File.Delete(cmdPath);
                    return;
                }

                await Task.Delay(_pollIntervalMs, ct);
            }

            throw new TimeoutException($"文件握手协议：等待确认超时（{_ackTimeoutMs}ms），设备可能未响应。");
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
        }

        // ── 扩展方法：发送完整打标指令并等待确认 ─────────────────────

        /// <summary>
        /// 发送打标指令（写入 cmd.txt），并阻塞等待设备完成确认（ack.txt 出现）。
        /// </summary>
        /// <param name="workpieceNo">工件编号</param>
        /// <param name="markContent">打标内容字符串</param>
        /// <param name="ct">取消令牌</param>
        public async Task SendMarkCommandAsync(string workpieceNo, string markContent, CancellationToken ct = default)
        {
            EnsureConnected();
            var ackPath = Path.Combine(_sharedDir, _ackFile);
            var errPath = Path.Combine(_sharedDir, _errFile);
            var cmdPath = Path.Combine(_sharedDir, _cmdFile);

            if (File.Exists(ackPath)) File.Delete(ackPath);
            if (File.Exists(errPath)) File.Delete(errPath);

            // 写入指令
            var content = $"WorkpieceNo={workpieceNo}\nMarkContent={markContent}\n";
            await File.WriteAllTextAsync(cmdPath, content, Encoding.UTF8, ct);

            // 等待 ack/err
            var deadline = DateTime.UtcNow.AddMilliseconds(_ackTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(errPath))
                {
                    string e = await File.ReadAllTextAsync(errPath, Encoding.UTF8, ct);
                    File.Delete(errPath);
                    File.Delete(cmdPath);
                    throw new IOException($"打标机报告错误：{e}");
                }
                if (File.Exists(ackPath))
                {
                    File.Delete(ackPath);
                    File.Delete(cmdPath);
                    return;
                }
                await Task.Delay(_pollIntervalMs, ct);
            }
            throw new TimeoutException("等待打标机完成超时。");
        }

        // ── 私有 ─────────────────────────────────────────────────────

        private void EnsureConnected()
        {
            if (!_isConnected)
                throw new InvalidOperationException("文件握手客户端未连接，请先调用 ConnectAsync()。");
        }

        /// <summary>
        /// 将整数地址映射为文件中的 key 名称。
        /// 可根据实际打标机文档扩展。
        /// </summary>
        private static string AddressToKey(int address) => address switch
        {
            1 => "WorkpieceNo",
            2 => "MarkContent",
            3 => "Status",
            4 => "ErrorCode",
            _ => $"Field{address}"
        };

        /// <summary>从 key=value 文本中提取指定 key 的值。</summary>
        private static string ParseKv(string content, string key)
        {
            foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = line.IndexOf('=');
                if (idx < 0) continue;
                if (line[..idx].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return line[(idx + 1)..].Trim();
            }
            return string.Empty;
        }
    }
}
