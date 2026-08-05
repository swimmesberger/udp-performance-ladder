using System.Net;
using System.Net.Sockets;
using Forwarder.Core;

namespace Forwarder.Rung3;

/// <summary>
/// The USO engine: plain sockets, but the send path hands the kernel one
/// packed buffer per batch and lets the stack split it into datagrams
/// (UDP_SEND_MSG_SIZE, Windows' analog of Linux UDP_SEGMENT / GSO,
/// Windows 10 2004+). The receive side stays one recv syscall per datagram
/// unless URO is enabled (UDP_RECV_MAX_COALESCED_SIZE, the GRO twin,
/// Windows 11 24H2+), in which case the stack may hand back several
/// same-flow datagrams as one contiguous blob, which this engine forwards
/// as one already-packed USO batch. URO forces an assumption of equal-size
/// datagrams; the --uro-segment option declares that size explicitly.
///
/// Same serial one-thread pipeline and zero steady-state allocation as the
/// other rung 3 engines; the changed variable is that the kernel does the
/// per-datagram send work once per batch instead of once per packet.
/// </summary>
internal sealed class UsoForwarder
{
    private const int UdpSendMsgSize = 2;          // ws2ipdef.h UDP_SEND_MSG_SIZE
    private const int UdpRecvMaxCoalescedSize = 3; // ws2ipdef.h UDP_RECV_MAX_COALESCED_SIZE
    private const int MaxSegments = 64;            // mirrors the Linux GSO engine
    private const int MaxDatagram = 2048;          // fits any non-jumbo datagram
    private const int PollMicroseconds = 100_000;  // idle wait, re-checks the token

    private readonly ForwarderOptions _options;
    private readonly ForwarderStats _stats;
    private readonly int _uroSegment;

    public UsoForwarder(ForwarderOptions options, ForwarderStats stats, int uroSegment)
    {
        _options = options;
        _stats = stats;
        _uroSegment = uroSegment;
    }

    public void Run(CancellationToken ct)
    {
        using var rx = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        rx.Bind(new IPEndPoint(IPAddress.Any, _options.ListenPort));
        rx.ReceiveBufferSize = 1 << 20;
        rx.DisableUdpConnReset();
        rx.Blocking = false;
        if (_uroSegment > 0)
        {
            rx.SetSocketOption(SocketOptionLevel.Udp,
                (SocketOptionName)UdpRecvMaxCoalescedSize,
                Math.Min(MaxSegments * _uroSegment, 65535));
        }

        var tx = new Socket[_options.Destinations.Count];
        var txSegment = new int[tx.Length];
        for (int i = 0; i < tx.Length; i++)
        {
            tx[i] = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            tx[i].Connect(_options.Destinations[i]);
            txSegment[i] = -1;
        }

        // Receives land directly at the batch's tail, so packing is copy-free.
        byte[] packed = GC.AllocateArray<byte>(MaxSegments * MaxDatagram + MaxDatagram, pinned: true);
        int packedBytes = 0;
        int packedSegments = 0;
        int segmentSize = 0;

        // Room one receive may need: a coalesced blob, or one datagram.
        int maxReceive = _uroSegment > 0
            ? Math.Min(MaxSegments * _uroSegment, 65535)
            : MaxDatagram;

        // One USO send carries at most 64 KB in total, so large payloads cap
        // the batch below MaxSegments (64 x 1200 B would be 76,800).
        int maxSegments = MaxSegments;
        if (_uroSegment > 0)
        {
            maxSegments = Math.Max(1, Math.Min(MaxSegments, 65535 / _uroSegment));
        }

        while (!ct.IsCancellationRequested)
        {
            if (packedSegments > 0 && packed.Length - packedBytes < maxReceive)
            {
                Flush(tx, txSegment, packed, ref packedBytes, ref packedSegments, segmentSize);
            }
            int n = rx.Receive(packed.AsSpan(packedBytes), SocketFlags.None, out SocketError error);
            if (error == SocketError.WouldBlock)
            {
                if (packedSegments > 0)
                {
                    Flush(tx, txSegment, packed, ref packedBytes, ref packedSegments, segmentSize);
                }
                rx.Poll(PollMicroseconds, SelectMode.SelectRead);
                continue;
            }
            if (error == SocketError.ConnectionReset)
            {
                continue; // ICMP port-unreachable from an earlier send; not a receive failure
            }
            if (error != SocketError.Success)
            {
                throw new SocketException((int)error);
            }

            // One receive carries one datagram, or (with URO engaged) several
            // equal-size ones back to back with a possibly-shorter tail. Both
            // cases feed the same packing path, so opting into URO is a
            // superset of plain USO rather than a different engine.
            int segment = _uroSegment > 0 ? Math.Min(_uroSegment, n) : n;
            int whole = n / segment;
            int tail = n - whole * segment;
            int segmentsInReceive = whole + (tail > 0 ? 1 : 0);

            for (int i = 0; i < whole; i++)
            {
                _stats.PacketReceived(segment);
            }
            if (tail > 0)
            {
                _stats.PacketReceived(tail);
            }

            // A segment-size change or a full pack ends the current batch:
            // USO needs equal-size segments. Flush, then move this receive
            // down to start the next batch.
            if (packedSegments > 0 &&
                (segment != segmentSize || packedSegments + segmentsInReceive > maxSegments))
            {
                int oddOffset = packedBytes;
                Flush(tx, txSegment, packed, ref packedBytes, ref packedSegments, segmentSize);
                Buffer.BlockCopy(packed, oddOffset, packed, 0, n);
            }
            segmentSize = segment;
            packedBytes += n;
            packedSegments += segmentsInReceive;

            // A short tail can only be the batch's last segment.
            if (tail > 0 || packedSegments >= maxSegments)
            {
                Flush(tx, txSegment, packed, ref packedBytes, ref packedSegments, segmentSize);
            }
        }
    }

    private void Flush(
        Socket[] tx, int[] txSegment, byte[] packed,
        ref int packedBytes, ref int packedSegments, int segmentSize)
    {
        for (int i = 0; i < tx.Length; i++)
        {
            if (txSegment[i] != segmentSize)
            {
                tx[i].SetSocketOption(SocketOptionLevel.Udp, (SocketOptionName)UdpSendMsgSize, segmentSize);
                txSegment[i] = segmentSize;
            }
            tx[i].Send(packed.AsSpan(0, packedBytes), SocketFlags.None, out SocketError error);
            if (error == SocketError.Success)
            {
                int remaining = packedBytes;
                while (remaining > 0)
                {
                    int segment = Math.Min(segmentSize, remaining);
                    _stats.PacketForwarded(segment);
                    remaining -= segment;
                }
            }
            else
            {
                for (int d = 0; d < packedSegments; d++)
                {
                    _stats.PacketDropped();
                }
            }
        }
        packedBytes = 0;
        packedSegments = 0;
    }
}
