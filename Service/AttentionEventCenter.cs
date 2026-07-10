using System;
using System.Collections.Generic;
using System.Linq;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>需要人工关注的事件类别；不用于普通轮询或重连日志。</summary>
public enum AttentionEventKind
{
    SafetyAlarm,
    Warning,
    Emergency
}

/// <summary>当前程序生命周期内的一条人工关注事件。</summary>
public sealed record AttentionEvent(
    long Sequence,
    DateTime OccurredAt,
    AttentionEventKind Kind,
    string Scope,
    string Source,
    string Message,
    string? Result)
{
    public string KindText => Kind switch
    {
        AttentionEventKind.SafetyAlarm => "红色安全异常",
        AttentionEventKind.Warning => "黄色警告",
        AttentionEventKind.Emergency => "应急操作",
        _ => "未知"
    };

    public string ResultText => string.IsNullOrWhiteSpace(Result) ? "--" : Result;
}

/// <summary>
/// 仅在内存保存当前程序生命周期内的人工关注事件。
/// 事件中心永远不能反向影响设备控制或生产流程。
/// </summary>
public sealed class AttentionEventCenter
{
    public const int Capacity = 500;

    private readonly object _gate = new();
    private readonly List<AttentionEvent> _events = new();
    private long _nextSequence;

    /// <summary>仅用于通知 UI 刷新；订阅方异常由 Record 兜底吞掉。</summary>
    public event Action? Changed;

    public void Record(AttentionEventKind kind, string scope, string source,
        string message, string? result = null)
    {
        try
        {
            lock (_gate)
            {
                _events.Add(new AttentionEvent(
                    ++_nextSequence,
                    DateTime.Now,
                    kind,
                    scope,
                    source,
                    message,
                    result));

                if (_events.Count > Capacity)
                    _events.RemoveAt(0);
            }

            Changed?.Invoke();
        }
        catch
        {
            // 监控故障不得影响安全告警、暂停、弹窗或任何设备动作。
        }
    }

    public IReadOnlyList<AttentionEvent> Snapshot()
    {
        lock (_gate)
            return _events.OrderByDescending(x => x.Sequence).ToArray();
    }
}
