using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace UdpBench;

public sealed record SendOptions(
    IPEndPoint Target,
    int Size,
    long Rate,
    int DurationSeconds,
    int Threads = 1);

public sealed record SendResult(
    long PacketsSent,
    double Seconds,
    double Pps,
    double PayloadMbit,
    int Threads);

public static class UdpSender
{
    /// <summary>
    /// Blasts the target from <see cref="SendOptions.Threads"/> threads, each
    /// with its own socket. One thread does one syscall per packet and tops
    /// out well below line rate on modest hardware, so saturating a fast
    /// forwarder needs several.
    /// </summary>
    public static SendResult Run(SendOptions options, Action<string>? progress, CancellationToken ct)
    {
        int threads = Math.Max(1, options.Threads);
        var counters = new long[threads];
        var stopwatch = Stopwatch.StartNew();

        using var reporter = progress is null
            ? null
            : StartReporter(counters, options, stopwatch, progress, ct);

        Parallel.For(0, threads, index =>
            SendLoop(index, threads, options, counters, stopwatch, ct));

        double seconds = stopwatch.Elapsed.TotalSeconds;
        long sent = 0;
        foreach (long count in counters)
        {
            sent += count;
        }

        return new SendResult(
            sent,
            seconds,
            sent / seconds,
            sent * options.Size * 8 / seconds / 1_000_000,
            threads);
    }

    private static void SendLoop(
        int index,
        int threads,
        SendOptions options,
        long[] counters,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        using var socket = new Socket(options.Target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.SendBufferSize = 4 << 20;
        socket.Connect(options.Target);

        byte[] payload = new byte[options.Size];
        var duration = TimeSpan.FromSeconds(options.DurationSeconds);
        // Each thread owns one residue class of the sequence space, so the
        // numbers stay globally contiguous and the sink's loss math still works.
        long sequence = index;
        long sent = 0;
        double threadRate = options.Rate > 0 ? (double)options.Rate / threads : 0;

        while (!ct.IsCancellationRequested
               && (options.DurationSeconds == 0 || stopwatch.Elapsed < duration))
        {
            if (threadRate > 0)
            {
                long due = (long)(stopwatch.Elapsed.TotalSeconds * threadRate);
                if (sent >= due)
                {
                    // Yield rather than spin. Busy-waiting here made threads
                    // starve each other on a machine with fewer cores than
                    // threads: a 16-thread run asked for 100k pps and
                    // delivered 31k. Sleeping wakes with a small deficit that
                    // the next iterations send as a burst.
                    Thread.Sleep(1);
                    continue;
                }
            }

            BinaryPrimitives.WriteInt64LittleEndian(payload, sequence);
            socket.Send(payload);
            sequence += threads;
            sent++;
            Volatile.Write(ref counters[index], sent);
        }
    }

    private static IDisposable StartReporter(
        long[] counters,
        SendOptions options,
        Stopwatch stopwatch,
        Action<string> progress,
        CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task loop = Task.Run(async () =>
        {
            long previous = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                long total = 0;
                for (int i = 0; i < counters.Length; i++)
                {
                    total += Volatile.Read(ref counters[i]);
                }
                long delta = total - previous;
                previous = total;
                progress($"tx {delta,11:N0} pps {delta * options.Size * 8 / 1_000_000.0,8:N1} Mbit/s");
            }
        }, cts.Token);

        return new Stopper(cts, loop);
    }

    private sealed class Stopper(CancellationTokenSource cts, Task loop) : IDisposable
    {
        public void Dispose()
        {
            cts.Cancel();
            try
            {
                loop.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                // reporter observed cancellation
            }
            cts.Dispose();
        }
    }
}
