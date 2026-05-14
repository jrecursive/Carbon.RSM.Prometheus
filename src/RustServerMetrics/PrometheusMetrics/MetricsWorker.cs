using System;
using System.Collections.Generic;
using System.Threading;

namespace RustServerMetrics.PrometheusMetrics;

internal sealed class MetricsWorker : IDisposable
{
    private const int DefaultMaxPendingWork = 8192;

    private readonly object _gate = new();
    private readonly int _maxPendingWork;
    private readonly List<WorkItem> _queue = new();
    private readonly Dictionary<string, WorkItem> _latest = new(StringComparer.Ordinal);
    private Thread _thread;
    private bool _accepting;
    private bool _stopping;
    private int _queueOffset;
    private int _executing;
    private long _coalescedCount;
    private long _droppedCount;
    private long _faultedCount;

    public MetricsWorker(int maxPendingWork = DefaultMaxPendingWork)
    {
        _maxPendingWork = Math.Max(1, maxPendingWork);
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _accepting && _thread?.IsAlive == true;
            }
        }
    }

    public int QueuedCount
    {
        get
        {
            lock (_gate)
            {
                return QueuedWorkCountNoLock() + _latest.Count;
            }
        }
    }

    public long CoalescedCount => Interlocked.Read(ref _coalescedCount);
    public long DroppedCount => Interlocked.Read(ref _droppedCount);
    public long FaultedCount => Interlocked.Read(ref _faultedCount);

    public void Start()
    {
        lock (_gate)
        {
            if (_thread?.IsAlive == true)
            {
                return;
            }

            _accepting = true;
            _stopping = false;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Carbon.RSM metrics worker"
            };
            _thread.Start();
        }
    }

    public bool Enqueue(Action action)
    {
        if (action == null)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_accepting || _stopping)
            {
                return false;
            }

            if (PendingWorkCountNoLock() >= _maxPendingWork)
            {
                Interlocked.Increment(ref _droppedCount);
                return false;
            }

            _queue.Add(new WorkItem(action));
            Monitor.Pulse(_gate);
            return true;
        }
    }

    public bool EnqueueLatest(string key, Action action)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Enqueue(action);
        }

        if (action == null)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_accepting || _stopping)
            {
                return false;
            }

            if (_latest.ContainsKey(key))
            {
                _latest[key] = new WorkItem(action);
                Interlocked.Increment(ref _coalescedCount);
                Monitor.Pulse(_gate);
                return true;
            }

            if (PendingWorkCountNoLock() >= _maxPendingWork)
            {
                Interlocked.Increment(ref _droppedCount);
                return false;
            }

            _latest.Add(key, new WorkItem(action));
            Monitor.Pulse(_gate);
            return true;
        }
    }

    public bool Flush(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow <= deadline)
        {
            if (QueuedCount == 0 && Volatile.Read(ref _executing) == 0)
            {
                return true;
            }

            Thread.Sleep(1);
        }

        return QueuedCount == 0 && Volatile.Read(ref _executing) == 0;
    }

    public void Stop()
    {
        Thread thread;

        lock (_gate)
        {
            thread = _thread;
            if (thread == null)
            {
                return;
            }

            _accepting = false;
            _stopping = true;
            Monitor.PulseAll(_gate);
        }

        if (Thread.CurrentThread != thread)
        {
            thread.Join();
        }

        lock (_gate)
        {
            if (_thread == thread)
            {
                _thread = null;
            }

            _queue.Clear();
            _latest.Clear();
            _queueOffset = 0;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void Run()
    {
        while (true)
        {
            var item = Dequeue();
            if (item.Action == null)
            {
                return;
            }

            Interlocked.Increment(ref _executing);
            try
            {
                item.Action.Invoke();
            }
            catch
            {
                Interlocked.Increment(ref _faultedCount);
            }
            finally
            {
                Interlocked.Decrement(ref _executing);
            }
        }
    }

    private WorkItem Dequeue()
    {
        lock (_gate)
        {
            while (QueuedWorkCountNoLock() == 0 && _latest.Count == 0)
            {
                if (_stopping)
                {
                    return default;
                }

                Monitor.Wait(_gate);
            }

            if (QueuedWorkCountNoLock() > 0)
            {
                var queuedItem = _queue[_queueOffset++];

                if (_queueOffset > 64 && _queueOffset * 2 >= _queue.Count)
                {
                    _queue.RemoveRange(0, _queueOffset);
                    _queueOffset = 0;
                }

                return queuedItem;
            }

            using var enumerator = _latest.GetEnumerator();
            if (!enumerator.MoveNext())
            {
                return default;
            }

            var key = enumerator.Current.Key;
            var item = enumerator.Current.Value;
            _latest.Remove(key);
            return item;
        }
    }

    private int PendingWorkCountNoLock()
    {
        return QueuedWorkCountNoLock() + _latest.Count + Volatile.Read(ref _executing);
    }

    private int QueuedWorkCountNoLock()
    {
        return _queue.Count - _queueOffset;
    }

    private readonly struct WorkItem
    {
        public readonly Action Action;

        public WorkItem(Action action)
        {
            Action = action;
        }
    }
}
