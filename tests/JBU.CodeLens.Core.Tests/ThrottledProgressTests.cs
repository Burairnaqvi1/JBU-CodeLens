using JBU.CodeLens.Shared.Utilities;

namespace JBU.CodeLens.Core.Tests;

/// <summary>
/// Covers the progress throttle that sits between the scan and the status bar.
/// </summary>
public class ThrottledProgressTests
{
    private sealed class Collector : IProgress<int>
    {
        private readonly List<int> _values = [];
        private readonly object _gate = new();

        public IReadOnlyList<int> Values
        {
            get
            {
                lock (_gate)
                {
                    return _values.ToList();
                }
            }
        }

        public void Report(int value)
        {
            lock (_gate)
            {
                _values.Add(value);
            }
        }
    }

    [Fact]
    public void FirstReport_IsForwardedImmediately()
    {
        var collector = new Collector();
        var throttle = new ThrottledProgress<int>(collector, TimeSpan.FromSeconds(30), v => v == -1);

        throttle.Report(1);

        Assert.Equal([1], collector.Values);
    }

    [Fact]
    public void ReportsInsideTheInterval_AreDropped()
    {
        var collector = new Collector();
        var throttle = new ThrottledProgress<int>(collector, TimeSpan.FromSeconds(30), v => v == -1);

        for (var i = 1; i <= 500; i++)
        {
            throttle.Report(i);
        }

        // A 30-second interval cannot elapse during the loop, so only the first survives.
        Assert.Equal([1], collector.Values);
    }

    [Fact]
    public void FinalReport_IsAlwaysForwardedEvenInsideTheInterval()
    {
        var collector = new Collector();
        var throttle = new ThrottledProgress<int>(collector, TimeSpan.FromSeconds(30), v => v == 99);

        throttle.Report(1);
        throttle.Report(2);   // dropped
        throttle.Report(99);  // final, must get through

        Assert.Equal([1, 99], collector.Values);
    }

    [Fact]
    public async Task ReportsAfterTheIntervalElapses_AreForwardedAgain()
    {
        var collector = new Collector();
        var throttle = new ThrottledProgress<int>(collector, TimeSpan.FromMilliseconds(30), v => v == -1);

        throttle.Report(1);
        await Task.Delay(80);
        throttle.Report(2);

        Assert.Equal([1, 2], collector.Values);
    }

    [Fact]
    public void ConcurrentReports_NeverForwardMoreThanOncePerInterval()
    {
        var collector = new Collector();
        var throttle = new ThrottledProgress<int>(collector, TimeSpan.FromSeconds(30), v => v == -1);

        // Reports arrive from several parse threads at once; the interval check must not let more
        // than one through per window just because the threads raced.
        Parallel.For(0, 2000, i => throttle.Report(i));

        Assert.Single(collector.Values);
    }

    [Fact]
    public void NullArguments_AreRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ThrottledProgress<int>(null!, TimeSpan.FromSeconds(1), _ => false));
        Assert.Throws<ArgumentNullException>(() =>
            new ThrottledProgress<int>(new Collector(), TimeSpan.FromSeconds(1), null!));
    }
}
