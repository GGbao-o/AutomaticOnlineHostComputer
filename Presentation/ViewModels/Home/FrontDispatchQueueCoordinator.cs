using System;
using System.Collections.Generic;
using System.Linq;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

internal enum FrontDispatchPauseOutcome
{
    Removed,
    NotQueued,
    Dispatching
}

/// <summary>
/// 总上料架待派发FIFO的唯一并发所有者。
/// 队列顺序、暂停移除和“已出队但尚未写入线路缓存”的派发占用在同一把锁内提交。
/// </summary>
internal sealed class FrontDispatchQueueCoordinator
{
    private readonly object _gate = new();
    private readonly Queue<Item> _queue = new();
    private Item? _dispatching;
    private long _version;

    internal sealed class Item
    {
        public Item(TaskRowViewModel row, WorkpieceCache workpiece)
        {
            Row = row ?? throw new ArgumentNullException(nameof(row));
            Workpiece = workpiece;
        }

        public TaskRowViewModel Row { get; }
        public WorkpieceCache Workpiece { get; }
    }

    internal readonly record struct OrderEntry(TaskRowViewModel Row, int Position);

    internal readonly record struct Snapshot(long Version, IReadOnlyList<OrderEntry> Entries);

    public int Count
    {
        get
        {
            lock (_gate)
                return _queue.Count;
        }
    }

    public WorkpieceCache[] GetQueuedWorkpieces()
    {
        lock (_gate)
            return _queue.Select(x => x.Workpiece).ToArray();
    }

    public bool TryPeek(out Item? item)
    {
        lock (_gate)
            return _queue.TryPeek(out item);
    }

    public bool TryEnqueue(
        TaskRowViewModel row,
        WorkpieceCache workpiece,
        out int position,
        out Snapshot snapshot)
    {
        lock (_gate)
        {
            if ((_dispatching != null && ReferenceEquals(_dispatching.Row, row))
                || _queue.Any(x => ReferenceEquals(x.Row, row)))
            {
                position = 0;
                snapshot = CreateSnapshotLocked();
                return false;
            }

            _queue.Enqueue(new Item(row, workpiece));
            position = _queue.Count;
            _version++;
            snapshot = CreateSnapshotLocked();
            return true;
        }
    }

    public FrontDispatchPauseOutcome TryPause(
        TaskRowViewModel row,
        out int oldPosition,
        out Snapshot snapshot)
    {
        lock (_gate)
        {
            if (_dispatching != null && ReferenceEquals(_dispatching.Row, row))
            {
                oldPosition = 0;
                snapshot = CreateSnapshotLocked();
                return FrontDispatchPauseOutcome.Dispatching;
            }

            oldPosition = 0;
            int currentPosition = 0;
            var kept = new Queue<Item>(_queue.Count);
            while (_queue.Count > 0)
            {
                var item = _queue.Dequeue();
                currentPosition++;
                if (oldPosition == 0 && ReferenceEquals(item.Row, row))
                {
                    oldPosition = currentPosition;
                    continue;
                }

                kept.Enqueue(item);
            }

            while (kept.Count > 0)
                _queue.Enqueue(kept.Dequeue());

            if (oldPosition == 0)
            {
                snapshot = CreateSnapshotLocked();
                return FrontDispatchPauseOutcome.NotQueued;
            }

            _version++;
            snapshot = CreateSnapshotLocked();
            return FrontDispatchPauseOutcome.Removed;
        }
    }

    public bool TryBeginDispatch(Item expected, out Item? item, out Snapshot snapshot)
    {
        lock (_gate)
        {
            if (_dispatching != null
                || !_queue.TryPeek(out var head)
                || !ReferenceEquals(head, expected))
            {
                item = null;
                snapshot = CreateSnapshotLocked();
                return false;
            }

            item = _queue.Dequeue();
            _dispatching = item;
            _version++;
            snapshot = CreateSnapshotLocked();
            return true;
        }
    }

    public Snapshot CompleteDispatch(Item item)
    {
        lock (_gate)
        {
            EnsureCurrentDispatchLocked(item);
            _dispatching = null;
            _version++;
            return CreateSnapshotLocked();
        }
    }

    public Snapshot RequeueHead(Item item)
    {
        lock (_gate)
        {
            EnsureCurrentDispatchLocked(item);

            var rebuilt = new Queue<Item>(_queue.Count + 1);
            rebuilt.Enqueue(item);
            while (_queue.Count > 0)
                rebuilt.Enqueue(_queue.Dequeue());
            while (rebuilt.Count > 0)
                _queue.Enqueue(rebuilt.Dequeue());

            _dispatching = null;
            _version++;
            return CreateSnapshotLocked();
        }
    }

    private Snapshot CreateSnapshotLocked()
    {
        var entries = _queue
            .Select((item, index) => new OrderEntry(item.Row, index + 1))
            .ToArray();
        return new Snapshot(_version, entries);
    }

    private void EnsureCurrentDispatchLocked(Item item)
    {
        if (_dispatching == null || !ReferenceEquals(_dispatching, item))
            throw new InvalidOperationException("派发项不是当前协调器持有的派发中任务");
    }
}
