using System.Diagnostics;

namespace Forwarder.Core;

public sealed class ForwarderStats
{
    private long _receivedPackets;
    private long _receivedBytes;
    private long _forwardedPackets;
    private long _forwardedBytes;

    public void PacketReceived(int bytes)
    {
        Interlocked.Increment(ref _receivedPackets);
        Interlocked.Add(ref _receivedBytes, bytes);
    }

    public void PacketForwarded(int bytes)
    {
        Interlocked.Increment(ref _forwardedPackets);
        Interlocked.Add(ref _forwardedBytes, bytes);
    }

    public (long RxPackets, long RxBytes, long TxPackets, long TxBytes) Snapshot() => (
        Interlocked.Read(ref _receivedPackets),
        Interlocked.Read(ref _receivedBytes),
        Interlocked.Read(ref _forwardedPackets),
        Interlocked.Read(ref _forwardedBytes));
}

public sealed class StatsReporter : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private StatsReporter(ForwarderStats stats, TimeSpan interval)
    {
        _loop = Task.Run(() => RunAsync(stats, interval, _cts.Token));
    }

    public static StatsReporter Start(ForwarderStats stats, TimeSpan interval) => new(stats, interval);

    private static async Task RunAsync(ForwarderStats stats, TimeSpan interval, CancellationToken ct)
    {
        var previous = stats.Snapshot();
        var stopwatch = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var current = stats.Snapshot();
            double seconds = stopwatch.Elapsed.TotalSeconds;
            stopwatch.Restart();

            double rxPps = (current.RxPackets - previous.RxPackets) / seconds;
            double rxMbit = (current.RxBytes - previous.RxBytes) * 8 / seconds / 1_000_000;
            double txPps = (current.TxPackets - previous.TxPackets) / seconds;
            double txMbit = (current.TxBytes - previous.TxBytes) * 8 / seconds / 1_000_000;

            Console.WriteLine(
                $"rx {rxPps,11:N0} pps {rxMbit,8:N1} Mbit/s | " +
                $"tx {txPps,11:N0} pps {txMbit,8:N1} Mbit/s | " +
                $"total rx {current.RxPackets:N0} tx {current.TxPackets:N0}");
            previous = current;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // reporter loop already observed cancellation
        }
        _cts.Dispose();
    }
}
