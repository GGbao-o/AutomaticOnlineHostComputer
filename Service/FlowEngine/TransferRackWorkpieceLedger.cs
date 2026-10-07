using System;
using System.Collections.Generic;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 中转架的工件身份账本。预约不删除来源身份；只有X11确认吸住后才能提交取走。
/// 所有方法在同一把私有锁内完成，调用方不得再把内部集合暴露给流程判断。
/// </summary>
internal sealed class TransferRackWorkpieceLedger
{
    private readonly object _sync = new();
    private readonly Dictionary<string, WorkpieceCache> _workpieces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _arrivalSequences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Reservation> _reservations = new(StringComparer.Ordinal);
    private long _nextArrivalSequence;

    public int Count { get { lock (_sync) return _workpieces.Count; } }

    public void Enqueue(string rackCode, WorkpieceCache workpiece, out long arrivalSequence)
    {
        lock (_sync)
        {
            _workpieces[rackCode] = workpiece;
            _reservations.Remove(rackCode);
            arrivalSequence = ++_nextArrivalSequence;
            _arrivalSequences[rackCode] = arrivalSequence;
        }
    }

    public bool TryEnqueueIfEmpty(string rackCode, WorkpieceCache workpiece, out long arrivalSequence)
    {
        lock (_sync)
        {
            if (_workpieces.ContainsKey(rackCode))
            {
                arrivalSequence = 0;
                return false;
            }
            _workpieces[rackCode] = workpiece;
            arrivalSequence = ++_nextArrivalSequence;
            _arrivalSequences[rackCode] = arrivalSequence;
            return true;
        }
    }

    public bool TryGet(string rackCode, out WorkpieceCache workpiece)
    {
        lock (_sync) return _workpieces.TryGetValue(rackCode, out workpiece);
    }
/// <summary>
/// 上料动作预约
/// </summary>
/// <param name="rackCode"></param>
/// <param name="operationId"></param>
/// <param name="workpiece"></param>
/// <param name="arrivalSequence"></param>
/// <returns></returns>
    public bool TryReserve(string rackCode, string operationId, out WorkpieceCache workpiece, out long arrivalSequence)
    {
        lock (_sync)
        {
            if (!_workpieces.TryGetValue(rackCode, out workpiece))
            {
                arrivalSequence = 0;
                return false;
            }
            if (_reservations.TryGetValue(rackCode, out Reservation? reservation) &&
                (!StringComparer.Ordinal.Equals(reservation.OperationId, operationId) || reservation.ManualResolution))
            {
                arrivalSequence = 0;
                return false;
            }
            _reservations[rackCode] = reservation ?? Reservation.Active(operationId);
            arrivalSequence = _arrivalSequences.TryGetValue(rackCode, out long seq) ? seq : 0;
            return true;
        }
    }
/// <summary>
/// 
/// </summary>
/// <param name="rackCode">设备编码</param>
/// <param name="operationId">操作id</param>
/// <param name="workpiece">返回的工件缓存信息</param>
/// <param name="arrivalSequence">成功时返回的序列号</param>
/// <returns></returns>
    public bool CommitPickup(string rackCode, string operationId, out WorkpieceCache workpiece, out long arrivalSequence)
    {
        //加锁
        lock (_sync)
        {
            workpiece = default;
            //ST105 还有没有预约；
            // 预约的 operationId 是不是本动作；
            // ST105 账本里那块工件还在不在（_workpieces.Remove）。
            if (!_reservations.TryGetValue(rackCode, out Reservation? reservation) || !StringComparer.Ordinal.Equals(reservation.OperationId, operationId) ||
                !_workpieces.Remove(rackCode, out workpiece))
            {
                arrivalSequence = 0;
                return false;
            }
            _reservations.Remove(rackCode);
            _arrivalSequences.Remove(rackCode, out arrivalSequence);
            return true;
        }
    }

    public bool ReleaseReservation(string rackCode, string operationId)
    {
        lock (_sync)
        {
            if (!_reservations.TryGetValue(rackCode, out Reservation? reservation) ||
                !StringComparer.Ordinal.Equals(reservation.OperationId, operationId) || reservation.ManualResolution) return false;
            return _reservations.Remove(rackCode);
        }
    }

    /// <summary>
    /// 已发送物理命令却无法确认结果时，来源身份仍留在架位且不得被下一轮抢占。
    /// 这不是长期持锁；只是一份必须由人工处置的缓存所有权事实。
    /// </summary>
    public bool HoldForManualResolution(string rackCode, string operationId, string reason)
    {
        lock (_sync)
        {
            if (!_reservations.TryGetValue(rackCode, out Reservation? reservation) ||
                !StringComparer.Ordinal.Equals(reservation.OperationId, operationId)) return false;
            _reservations[rackCode] = reservation.WithManualResolution(reason);
            return true;
        }
    }

    public bool IsHeldForManualResolution(string rackCode, out string operationId, out string reason)
    {
        lock (_sync)
        {
            if (_reservations.TryGetValue(rackCode, out Reservation? reservation) && reservation.ManualResolution)
            {
                operationId = reservation.OperationId;
                reason = reservation.Reason;
                return true;
            }
            operationId = string.Empty;
            reason = string.Empty;
            return false;
        }
    }

    public bool IsReserved(string rackCode, out string operationId)
    {
        lock (_sync)
        {
            if (_reservations.TryGetValue(rackCode, out Reservation? reservation))
            {
                operationId = reservation.OperationId;
                return true;
            }
            operationId = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// 只能由人工处置入口调用。StillAtSource 仅解除占用，其余结论从来源账本移除工件，
    /// 由调用方把返回的工件登记到天车任务牌、目标待交接牌或人工移走记录。
    /// </summary>
    public bool TryResolveManualReservation(
        string rackCode,
        string operationId,
        FlowActionManualResolution resolution,
        out WorkpieceCache workpiece,
        out long arrivalSequence)
    {
        lock (_sync)
        {
            workpiece = default;
            arrivalSequence = 0;
            if (!_reservations.TryGetValue(rackCode, out Reservation? reservation) ||
                !reservation.ManualResolution ||
                !StringComparer.Ordinal.Equals(reservation.OperationId, operationId) ||
                !_workpieces.TryGetValue(rackCode, out workpiece))
                return false;

            arrivalSequence = _arrivalSequences.TryGetValue(rackCode, out long sequence) ? sequence : 0;
            _reservations.Remove(rackCode);
            if (resolution == FlowActionManualResolution.StillAtSource)
                return true;

            _workpieces.Remove(rackCode);
            _arrivalSequences.Remove(rackCode);
            return true;
        }
    }

    public Dictionary<string, WorkpieceCache> SnapshotWorkpieces()
    {
        lock (_sync) return new Dictionary<string, WorkpieceCache>(_workpieces, StringComparer.Ordinal);
    }

    public Dictionary<string, long> SnapshotArrivalSequences()
    {
        lock (_sync) return new Dictionary<string, long>(_arrivalSequences, StringComparer.Ordinal);
    }

    public void Clear()
    {
        lock (_sync)
        {
            _workpieces.Clear();
            _arrivalSequences.Clear();
            _reservations.Clear();
            _nextArrivalSequence = 0;
        }
    }

    private sealed record Reservation(string OperationId, bool ManualResolution, string Reason)
    {
        public static Reservation Active(string operationId) => new(operationId, false, string.Empty);
        public Reservation WithManualResolution(string reason) => this with { ManualResolution = true, Reason = reason };
    }
}
