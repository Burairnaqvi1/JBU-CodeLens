using System.Diagnostics;

namespace JBU.CodeLens.Shared.Utilities;

/// <summary>
/// Forwards progress reports to an inner handler at most once per interval, always letting the
/// final report through.
/// </summary>
/// <remarks>
/// <para>
/// The scan reports once per file, which is the right granularity for the engine but not for a
/// screen: a few thousand files means a few thousand updates, each one marshalled to the UI thread
/// to format a string and re-lay out a line of text that changes far faster than anyone can read.
/// Throttling here drops the forwarded reports rather than the work inside them, so the cost is
/// removed before it reaches the dispatcher at all.
/// </para>
/// <para>
/// Reports arrive from several parse threads at once, so the interval check is lock-free: threads
/// race for the slot and only the winner forwards.
/// </para>
/// </remarks>
public sealed class ThrottledProgress<T> : IProgress<T>
{
    private readonly IProgress<T> _inner;
    private readonly long _minimumTicks;
    private readonly Func<T, bool> _isFinal;
    private long _lastForwardedTimestamp;

    /// <param name="inner">The handler that receives the reports that get through.</param>
    /// <param name="minimumInterval">Shortest gap between forwarded reports.</param>
    /// <param name="isFinal">
    /// Identifies the last report, which is always forwarded so the display never settles on a
    /// stale value.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> or <paramref name="isFinal"/> is null.</exception>
    public ThrottledProgress(IProgress<T> inner, TimeSpan minimumInterval, Func<T, bool> isFinal)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(isFinal);

        _inner = inner;
        _isFinal = isFinal;
        _minimumTicks = (long)(Stopwatch.Frequency * minimumInterval.TotalSeconds);

        // Start in the past so the first report is never withheld.
        _lastForwardedTimestamp = Stopwatch.GetTimestamp() - _minimumTicks;
    }

    /// <inheritdoc />
    public void Report(T value)
    {
        if (_isFinal(value))
        {
            Volatile.Write(ref _lastForwardedTimestamp, Stopwatch.GetTimestamp());
            _inner.Report(value);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var last = Volatile.Read(ref _lastForwardedTimestamp);
        if (now - last < _minimumTicks)
        {
            return;
        }

        // Whichever thread claims the slot forwards; the others skip this interval rather than
        // queueing behind it.
        if (Interlocked.CompareExchange(ref _lastForwardedTimestamp, now, last) != last)
        {
            return;
        }

        _inner.Report(value);
    }
}
