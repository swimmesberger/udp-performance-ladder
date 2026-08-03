using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace UdpBench;

public sealed record SinkOptions(int Port, int DurationSeconds);

public sealed record SinkResult(
    long Packets,
    long Bytes,
    long MinSequence,
    long MaxSequence,
    long Expected,
    long Lost,
    double LossPercent);

public static class UdpSink
{
    public static SinkResult Run(SinkOptions options, Action<string>? progress, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveBufferSize = 8 << 20;
        // On Linux, closing the socket from another thread does not reliably
        // wake a blocked synchronous Receive, so the loop polls with a short
        // timeout and re-checks the cancellation and duration conditions.
        socket.ReceiveTimeout = 250;
        socket.Bind(new IPEndPoint(IPAddress.Any, options.Port));

        byte[] buffer = GC.AllocateArray<byte>(65536, pinned: true);
        long packets = 0;
        long bytes = 0;
        long minSequence = long.MaxValue;
        long maxSequence = -1;
        long packetsAtLastReport = 0;
        long bytesAtLastReport = 0;
        long nextReportMs = 1000;

        var duration = TimeSpan.FromSeconds(options.DurationSeconds);
        var stopwatch = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested
               && (options.DurationSeconds == 0 || stopwatch.Elapsed < duration))
        {
            if (progress is not null && stopwatch.ElapsedMilliseconds >= nextReportMs)
            {
                long deltaPackets = packets - packetsAtLastReport;
                double mbit = (bytes - bytesAtLastReport) * 8 / 1_000_000.0;
                progress($"rx {deltaPackets,11:N0} pps {mbit,8:N1} Mbit/s | total {packets:N0}");
                packetsAtLastReport = packets;
                bytesAtLastReport = bytes;
                nextReportMs += 1000;
            }

            int received;
            try
            {
                received = socket.Receive(buffer);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }

            packets++;
            bytes += received;
            if (received >= 8)
            {
                long sequence = BinaryPrimitives.ReadInt64LittleEndian(buffer);
                if (sequence < minSequence) minSequence = sequence;
                if (sequence > maxSequence) maxSequence = sequence;
            }
        }

        long expected = maxSequence >= 0 ? maxSequence - minSequence + 1 : 0;
        long lost = expected - packets;
        return new SinkResult(
            packets,
            bytes,
            maxSequence >= 0 ? minSequence : 0,
            maxSequence,
            expected,
            lost,
            expected > 0 ? 100.0 * lost / expected : 0);
    }
}
