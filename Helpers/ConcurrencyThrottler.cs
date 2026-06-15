namespace M3U8Downloader.Helpers;

public class ConcurrencyThrottler
{
    private readonly object _lock = new();
    private int _max;
    private int _active;
    private readonly Queue<TaskCompletionSource<bool>> _queue = new();

    public ConcurrencyThrottler(int max)
    {
        _max = Math.Max(1, max);
    }

    public int Max
    {
        get { lock (_lock) return _max; }
    }

    public int Active
    {
        get { lock (_lock) return _active; }
    }

    public Task AcquireAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_active < _max)
            {
                _active++;
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ct.Register(() => tcs.TrySetCanceled(ct));
            _queue.Enqueue(tcs);
            return tcs.Task;
        }
    }

    public void Release()
    {
        TaskCompletionSource<bool>? next = null;
        lock (_lock)
        {
            _active--;
            next = DequeueNext();
        }
        next?.TrySetResult(true);
    }

    public void SetMax(int newMax)
    {
        newMax = Math.Max(1, newMax);
        var toDispatch = new List<TaskCompletionSource<bool>>();

        lock (_lock)
        {
            _max = newMax;
            while (_active < _max && _queue.Count > 0)
            {
                var tcs = DequeueNext();
                if (tcs != null)
                {
                    _active++;
                    toDispatch.Add(tcs);
                }
            }
        }

        foreach (var tcs in toDispatch)
            tcs.TrySetResult(true);
    }

    private TaskCompletionSource<bool>? DequeueNext()
    {
        while (_queue.Count > 0)
        {
            var tcs = _queue.Dequeue();
            if (!tcs.Task.IsCompleted)
                return tcs;
        }
        return null;
    }
}
