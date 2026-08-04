using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AutomaticOnlineHostComputer.Infrastructure.Logging;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices;

/// <summary>
/// 在独立后台线程中探测打号机UNC共享目录。
/// <para>UNC探测是不可取消的同步调用；独立线程保证目标离线时不会阻塞生产主循环，也不会产生重叠探测。</para>
/// </summary>
public sealed class MarkerShareMonitor : IDisposable
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AuthenticationRetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LogHeartbeat = TimeSpan.FromSeconds(30);

    private readonly string _name;
    private readonly string _sharePath;
    private readonly string? _username;
    private readonly string? _password;
    private readonly string _logScope;
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Thread? _worker;
    private MarkerShareSnapshot _snapshot = MarkerShareSnapshot.Disconnected("尚未探测");
    private bool? _lastLoggedConnected;
    private DateTime _lastLogAtUtc;
    private DateTime _nextAuthenticationAtUtc = DateTime.MinValue;
    private bool _shareSessionEstablished;
    private bool _disposed;

    public MarkerShareMonitor(string name, string sharePath, string? username = null, string? password = null,
        string logScope = EngineLogRouter.Shared)
    {
        _name = name;
        _sharePath = sharePath;
        _username = username;
        _password = password;
        _logScope = logScope;
    }

    /// <summary>最近一次探测成功且快照未过期时返回true。</summary>
    public bool IsFreshAndConnected
    {
        get
        {
            lock (_sync)
            {
                return _snapshot.Connected &&
                       DateTime.UtcNow - _snapshot.CapturedAtUtc <= SnapshotMaxAge;
            }
        }
    }

    public MarkerShareSnapshot LatestSnapshot
    {
        get { lock (_sync) return _snapshot; }
    }

    public void Start(CancellationToken ownerToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_worker?.IsAlive == true) return;

        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ownerToken);
        var ct = _cts.Token;
        _worker = new Thread(() => WorkerLoop(ct))
        {
            IsBackground = true,
            Name = $"MarkerShare-{_name}"
        };
        _worker.Start();
    }

    private void WorkerLoop(CancellationToken ct)
    {
        using var logScope = EngineLogRouter.BeginScope(_logScope);
        while (!ct.IsCancellationRequested)
        {
            bool connected;
            string? error = null;
            try
            {
                if (!_shareSessionEstablished && DateTime.UtcNow >= _nextAuthenticationAtUtc)
                {
                    error = TryEstablishShareSession();
                    _nextAuthenticationAtUtc = DateTime.UtcNow + AuthenticationRetryInterval;
                }

                connected = Directory.Exists(_sharePath);
                if (connected)
                {
                    _shareSessionEstablished = true;
                    error = null;
                }
                else
                {
                    _shareSessionEstablished = false;
                    error ??= "共享目录不可访问";
                }
            }
            catch (Exception ex)
            {
                connected = false;
                error = ex.Message;
            }

            Publish(new MarkerShareSnapshot(connected, DateTime.UtcNow, error));
            if (ct.WaitHandle.WaitOne(ProbeInterval)) break;
        }
    }

    /// <summary>
    /// 建立Windows共享会话。该同步调用只在本类专用后台线程执行，不会阻塞生产主循环或UI线程。
    /// </summary>
    private string? TryEstablishShareSession()
    {
        if (string.IsNullOrWhiteSpace(_username)) return null;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("net")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("use");
            process.StartInfo.ArgumentList.Add(_sharePath);
            process.StartInfo.ArgumentList.Add($"/user:{_username}");
            process.StartInfo.ArgumentList.Add(_password ?? string.Empty);

            if (!process.Start()) return "无法启动共享认证命令";
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "共享认证超时";
            }

            if (process.ExitCode == 0) return null;
            string detail = process.StandardError.ReadToEnd().Trim();
            if (string.IsNullOrWhiteSpace(detail)) detail = process.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrWhiteSpace(detail)
                ? $"共享认证失败（退出码{process.ExitCode}）"
                : $"共享认证失败（退出码{process.ExitCode}）：{detail}";
        }
        catch (Exception ex)
        {
            return $"共享认证异常：{ex.Message}";
        }
    }

    private void Publish(MarkerShareSnapshot snapshot)
    {
        bool shouldLog;
        lock (_sync)
        {
            _snapshot = snapshot;
            shouldLog = _lastLoggedConnected != snapshot.Connected ||
                        snapshot.CapturedAtUtc - _lastLogAtUtc >= LogHeartbeat;
            if (shouldLog)
            {
                _lastLoggedConnected = snapshot.Connected;
                _lastLogAtUtc = snapshot.CapturedAtUtc;
            }
        }

        if (shouldLog)
        {
            Console.WriteLine($"[MarkerShare] [{_name}] {(snapshot.Connected ? "共享目录可访问" : $"共享目录不可访问: {snapshot.Error}")} {_sharePath}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();

        // Directory.Exists本身不可取消，只做有限等待；后台线程不会阻塞进程退出。
        if (_worker?.IsAlive == true && !_worker.Join(TimeSpan.FromSeconds(2)))
            Console.WriteLine($"[MarkerShare] [{_name}] UNC探测仍在阻塞，后台线程将在调用返回后自行退出");

        if (_worker?.IsAlive != true)
        {
            _cts?.Dispose();
            _cts = null;
            _worker = null;
        }
    }
}

public sealed record MarkerShareSnapshot(bool Connected, DateTime CapturedAtUtc, string? Error)
{
    public static MarkerShareSnapshot Disconnected(string error) =>
        new(false, DateTime.MinValue, error);
}
