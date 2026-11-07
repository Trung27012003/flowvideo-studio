using System.Collections.Concurrent;

namespace Studio;

// Single-process cancellation. Durable truth remains the job status in SQLite.
public sealed class JobControl
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> active = new();

    public CancellationTokenSource Register(Guid id, CancellationToken stopping)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        if (!active.TryAdd(id, source))
        {
            source.Dispose();
            throw new InvalidOperationException("Job already active");
        }
        return source;
    }

    public void Cancel(Guid id)
    {
        if (active.TryGetValue(id, out var source))
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException) { }
        }
    }

    public void Remove(Guid id)
    {
        if (active.TryRemove(id, out var source))
            source.Dispose();
    }
}
