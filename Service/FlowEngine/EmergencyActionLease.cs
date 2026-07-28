using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// Owns cancellation, completion and software locks for one in-flight action.
/// Emergency cleanup and normal finally blocks share the same idempotent release path.
/// </summary>
internal sealed class EmergencyActionLease : IDisposable
{
    private readonly ConcurrentDictionary<string, byte> _heldLocks = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _valid = 1;
    private int _disposed;
    private string? _manualSafetyHoldReason;

    public EmergencyActionLease(long version, string name, CancellationToken parent)
    {
        Version = version;
        Name = name;
        Cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent);
    }

    public long Version { get; }
    public string Name { get; }
    public CancellationTokenSource Cancellation { get; }
    public CancellationToken Token => Cancellation.Token;
    public Task Completion => _completion.Task;
    public bool IsValid => Volatile.Read(ref _valid) != 0;
    /// <summary>
    /// A motion command timed out after its actual position became unknown.  The owner must
    /// retain every held physical-area lock until an operator completes emergency recovery.
    /// </summary>
    public bool RequiresManualSafetyRecovery => Volatile.Read(ref _manualSafetyHoldReason) != null;
    public string? ManualSafetyHoldReason => Volatile.Read(ref _manualSafetyHoldReason);

    public bool IsHeld(string key) => _heldLocks.ContainsKey(key);

    public void MarkHeld(string key)
    {
        if (!IsValid)
            throw new OperationCanceledException($"动作{Name}已失效", Token);
        _heldLocks[key] = 0;
    }

    public async Task AcquireAsync(string key, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        if (!IsValid)
        {
            gate.Release();
            throw new OperationCanceledException($"动作{Name}已失效", Token);
        }
        _heldLocks[key] = 0;
    }

    public bool TryRelease(string key, SemaphoreSlim? gate)
    {
        if (!_heldLocks.TryRemove(key, out _)) return false;
        if (gate == null) return true;
        try { gate.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
        return true;
    }

    public void CancelAndInvalidate()
    {
        Interlocked.Exchange(ref _valid, 0);
        try { Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void HoldForManualSafetyRecovery(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("安全占用原因不能为空", nameof(reason));
        Interlocked.CompareExchange(ref _manualSafetyHoldReason, reason, null);
    }

    public void ThrowIfInvalid()
    {
        if (!IsValid) throw new OperationCanceledException($"动作{Name}已失效", Token);
        Token.ThrowIfCancellationRequested();
    }

    public void Complete() => _completion.TrySetResult(true);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Cancellation.Dispose();
    }
}
