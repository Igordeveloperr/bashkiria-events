using System.Diagnostics.Metrics;

namespace Bashkiria.Events.Interaction.Api;

public sealed class ApiMetrics : IDisposable
{
    private readonly Meter _meter = new("Bashkiria.Events.Interaction");
    private readonly Counter<long> _requests;
    private readonly Histogram<double> _duration;
    private long _count, _errors, _elapsedMicroseconds;
    public ApiMetrics()
    {
        _requests = _meter.CreateCounter<long>("interaction.http.requests");
        _duration = _meter.CreateHistogram<double>("interaction.http.duration", "ms");
    }
    public void Record(int status, double milliseconds)
    {
        Interlocked.Increment(ref _count);
        if (status >= 400) Interlocked.Increment(ref _errors);
        Interlocked.Add(ref _elapsedMicroseconds, (long)(milliseconds * 1000));
        _requests.Add(1, new KeyValuePair<string, object?>("status", status)); _duration.Record(milliseconds);
    }
    public object Snapshot() => new { requests = Interlocked.Read(ref _count), errors = Interlocked.Read(ref _errors),
        meanDurationMs = Interlocked.Read(ref _elapsedMicroseconds) / 1000d / Math.Max(1, Interlocked.Read(ref _count)) };
    public void Dispose() => _meter.Dispose();
}
