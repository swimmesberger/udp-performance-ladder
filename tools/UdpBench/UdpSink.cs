using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace UdpBench;

public sealed record SinkOptions(int Port, int DurationSeconds, int Threads = 1);

public sealed record SinkResult(
    long Packets,
    long Bytes,
    long MinSequence,
    long MaxSequence,
    long Expected,
    long Lost,
    double LossPercent,
    int Threads);

public static class UdpSink
{
    /// <summary>
    /// Receives on <see cref="SinkOptions.Threads"/> threads sharing one
    /// socket. A single receive loop tops out well below what a fast
    /// forwarder can deliver over the wire (SO_REUSEPORT would not help:
    /// all traffic is one flow and would hash to a single socket).
    /// </summary>
    public static SinkResult Run(SinkOptions options, Action<string>? progress, CancellationToken ct)
    {
        int threads = Math.Max(1, options.Threads);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveBufferSize = 8 << 20;
        // On Linux, closing the socket from another thread does not reliably
        // wake a blocked synchronous Receive, so the loops poll with a short
        // timeout and re-check the cancellation and duration conditions.
        socket.ReceiveTimeout = 250;
        socket.Bind(new IPEndPoint(IPAddress.Any, options.Port));

        var duration = TimeSpan.FromSeconds(options.DurationSeconds);
        var stopwatch = Stopwatch.StartNew();
        bool Running() => !ct.IsCancellationRequested
            && (options.DurationSeconds == 0 || stopwatch.Elapsed < duration);

        var counters = new ThreadCounters[threads];
        var workers = new Thread[threads];
        for (int i = 0; i < threads; i++)
        {
            ThreadCounters mine = counters[i] = new ThreadCounters();
            workers[i] = new Thread(() => ReceiveLoop(socket, mine, Running)) { IsBackground = true };
            workers[i].Start();
        }

        long reportedPackets = 0;
        long reportedBytes = 0;
        long nextReportMs = 1000;
        while (Running())
        {
            Thread.Sleep(100);
            if (progress is not null && stopwatch.ElapsedMilliseconds >= nextReportMs)
            {
                long packets = 0;
                long bytes = 0;
                foreach (ThreadCounters c in counters)
                {
                    packets += Volatile.Read(ref c.Packets);
                    bytes += Volatile.Read(ref c.Bytes);
                }
                progress($"rx {packets - reportedPackets,11:N0} pps " +
                    $"{(bytes - reportedBytes) * 8 / 1_000_000.0,8:N1} Mbit/s | total {packets:N0}");
                reportedPackets = packets;
                reportedBytes = bytes;
                nextReportMs += 1000;
            }
        }

        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        long totalPackets = 0;
        long totalBytes = 0;
        long minSequence = long.MaxValue;
        long maxSequence = -1;
        foreach (ThreadCounters c in counters)
        {
            totalPackets += c.Packets;
            totalBytes += c.Bytes;
            if (c.MinSequence < minSequence) minSequence = c.MinSequence;
            if (c.MaxSequence > maxSequence) maxSequence = c.MaxSequence;
        }

        long expected = maxSequence >= 0 ? maxSequence - minSequence + 1 : 0;
        long lost = expected - totalPackets;
        return new SinkResult(
            totalPackets,
            totalBytes,
            maxSequence >= 0 ? minSequence : 0,
            maxSequence,
            expected,
            lost,
            expected > 0 ? 100.0 * lost / expected : 0,
            threads);
    }

    private static void ReceiveLoop(Socket socket, ThreadCounters counters, Func<bool> running)
    {
        if (OperatingSystem.IsLinux())
        {
            LinuxBatchIo.ReceiveLoop(socket, counters, running);
            return;
        }

        byte[] buffer = GC.AllocateArray<byte>(65536, pinned: true);
        while (running())
        {
            int received;
            try
            {
                received = socket.Receive(buffer);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }
            catch (SocketException)
            {
                break; // socket closed during shutdown
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            counters.Packets++;
            counters.Bytes += received;
            if (received >= 8)
            {
                long sequence = BinaryPrimitives.ReadInt64LittleEndian(buffer);
                if (sequence < counters.MinSequence) counters.MinSequence = sequence;
                if (sequence > counters.MaxSequence) counters.MaxSequence = sequence;
            }
        }
    }

}

internal sealed class ThreadCounters
{
    public long Packets;
    public long Bytes;
    public long MinSequence = long.MaxValue;
    public long MaxSequence = -1;
}
