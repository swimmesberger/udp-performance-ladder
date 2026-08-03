using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace UdpBench;

public sealed record SendOptions(IPEndPoint Target, int Size, long Rate, int DurationSeconds);

public sealed record SendResult(long PacketsSent, double Seconds, double Pps, double PayloadMbit);

public static class UdpSender
{
    public static SendResult Run(SendOptions options, Action<string>? progress, CancellationToken ct)
    {
        using var socket = new Socket(options.Target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.SendBufferSize = 4 << 20;
        socket.Connect(options.Target);

        byte[] payload = new byte[options.Size];
        var duration = TimeSpan.FromSeconds(options.DurationSeconds);
        var stopwatch = Stopwatch.StartNew();
        long sent = 0;
        long sentAtLastReport = 0;
        long nextReportMs = 1000;

        while (!ct.IsCancellationRequested
               && (options.DurationSeconds == 0 || stopwatch.Elapsed < duration))
        {
            if (options.Rate > 0)
            {
                long due = (long)(stopwatch.Elapsed.TotalSeconds * options.Rate);
                if (sent >= due)
                {
                    Thread.SpinWait(64);
                    continue;
                }
            }

            BinaryPrimitives.WriteInt64LittleEndian(payload, sent);
            socket.Send(payload);
            sent++;

            if (progress is not null && stopwatch.ElapsedMilliseconds >= nextReportMs)
            {
                long delta = sent - sentAtLastReport;
                progress($"tx {delta,11:N0} pps {delta * options.Size * 8 / 1_000_000.0,8:N1} Mbit/s");
                sentAtLastReport = sent;
                nextReportMs += 1000;
            }
        }

        double seconds = stopwatch.Elapsed.TotalSeconds;
        return new SendResult(
            sent,
            seconds,
            sent / seconds,
            sent * options.Size * 8 / seconds / 1_000_000);
    }
}
