using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices;

/// <summary>
/// MC协议连接缓存，相同IP:端口复用已有连接，避免多引擎重复连同一个PLC。
/// </summary>
public class McConnectionCache
{
    private readonly ConcurrentDictionary<string, MitsubishiMcClient> _clients = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionLocks = new();

    /// <summary>获取或创建连接</summary>
    public async Task<MitsubishiMcClient> GetOrCreateAsync(string ip, int port, CancellationToken ct = default)
    {
        var key = $"{ip}:{port}";
        if (_clients.TryGetValue(key, out var c) && c.IsConnected) return c;

        var gate = _connectionLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_clients.TryGetValue(key, out c) && c.IsConnected) return c;
            c = new MitsubishiMcClient(ip, port, MitsubishiMcClient.DeviceM,
                frameType: MitsubishiMcClient.McFrameType.A1E) { UseBitReadForM = false };
            await c.ConnectAsync(ct).ConfigureAwait(false);
            _clients[key] = c;
            Console.WriteLine($"[McCache] 新连接 {key} ✓");
            return c;
        }
        finally { gate.Release(); }
    }

    /// <summary>直接获取(不自动连接)，已连则返回</summary>
    public MitsubishiMcClient? TryGet(string ip, int port)
    {
        _clients.TryGetValue($"{ip}:{port}", out var c);
        return c?.IsConnected == true ? c : null;
    }

    /// <summary>读写失败时主动丢弃缓存连接，下一轮强制重连。</summary>
    public async Task InvalidateAsync(string ip, int port)
    {
        var key = $"{ip}:{port}";
        var gate = _connectionLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_clients.TryRemove(key, out var client)) return;

            try { await client.DisconnectAsync().ConfigureAwait(false); }
            catch { /* 失效清理不影响主流程 */ }
            Console.WriteLine($"[McCache] 连接失效已移除 {key}");
        }
        finally { gate.Release(); }
    }
}
