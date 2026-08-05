using System.Runtime.InteropServices;
using Forwarder.Core;

namespace Forwarder.Rung3.Linux;

/// <summary>
/// mmsg receive plus UDP GSO send: the last lever inside the socket API.
/// A batch of equal-size payloads is packed into one buffer and handed to
/// the kernel with a UDP_SEGMENT cmsg; the kernel splits it into packets
/// after a single traversal of most of the stack. The pack is a copy per
/// datagram, traded for sends dropping from one syscall per batch to one
/// stack traversal per batch. QUIC stacks ship on exactly this.
///
/// With <c>gro</c> enabled the receive side opts into UDP_GRO, the receive
/// twin: the kernel may deliver several same-flow, equal-size datagrams as
/// one coalesced buffer, with the segment size in a UDP_GRO cmsg. A
/// coalesced blob is already a packed GSO batch, so it is forwarded
/// straight from the receive slot with zero copies. GRO is software in the
/// kernel receive path; no NIC support required.
/// </summary>
internal static unsafe partial class GsoEngine
{
    private const int BatchSize = 64;         // == UDP_MAX_SEGMENTS
    private const int SlotSize = 2048;
    private const int SOL_UDP = 17;
    private const int UDP_SEGMENT = 103;
    private const int UDP_GRO = 104;
    private const int CmsgSpace = 24;         // CMSG_SPACE(4) on 64-bit

    [StructLayout(LayoutKind.Sequential)]
    private struct CmsgSegment
    {
        public nuint Length;   // CMSG_LEN(2) = 18
        public int Level;      // SOL_UDP
        public int Type;       // UDP_SEGMENT
        public ushort GsoSize;
    }

    [LibraryImport("libc", EntryPoint = "sendmsg", SetLastError = true)]
    private static partial nint SendMsg(int fd, Msghdr* msg, int flags);

    /// <param name="plainRx">
    /// Receive one datagram per syscall (recvmsg) instead of batching with
    /// recvmmsg. This is the structural twin of the Windows USO engine:
    /// send-side stack batching only, so the two OSes' segmentation-offload
    /// engines differ in the offload, not in how they receive.
    /// </param>
    public static void Run(ForwarderOptions options, ForwarderStats stats, bool gro, bool plainRx, CancellationToken ct)
    {
        int fd = Libc.CreateBoundUdpSocket(options.ListenPort, 1 << 20);
        int destinationCount = options.Destinations.Count;
        if (gro)
        {
            int one = 1;
            if (Libc.SetSockOpt(fd, SOL_UDP, UDP_GRO, &one, sizeof(int)) != 0)
            {
                throw new InvalidOperationException($"setsockopt(UDP_GRO) failed: errno {Marshal.GetLastWin32Error()}");
            }
        }

        byte* data = (byte*)NativeMemory.AllocZeroed(BatchSize * (nuint)SlotSize);
        byte* packed = (byte*)NativeMemory.AllocZeroed(BatchSize * (nuint)SlotSize);
        byte* sourceAddrs = (byte*)NativeMemory.AllocZeroed(BatchSize * (nuint)Libc.SockAddrInSize);
        byte* destAddrs = (byte*)NativeMemory.AllocZeroed((nuint)(destinationCount * Libc.SockAddrInSize));
        var recvIov = (Iovec*)NativeMemory.AllocZeroed((nuint)(BatchSize * sizeof(Iovec)));
        var recvVec = (Mmsghdr*)NativeMemory.AllocZeroed((nuint)(BatchSize * sizeof(Mmsghdr)));
        var cmsg = (CmsgSegment*)NativeMemory.AllocZeroed(24); // CMSG_SPACE(2)
        var sendIov = (Iovec*)NativeMemory.AllocZeroed((nuint)sizeof(Iovec));
        byte* recvCtrl = gro ? (byte*)NativeMemory.AllocZeroed(BatchSize * (nuint)CmsgSpace) : null;

        for (int d = 0; d < destinationCount; d++)
        {
            Libc.WriteSockAddr(destAddrs + d * Libc.SockAddrInSize, options.Destinations[d]);
        }
        for (int i = 0; i < BatchSize; i++)
        {
            recvIov[i].Base = data + i * SlotSize;
            recvIov[i].Length = SlotSize;
            recvVec[i].Header.Name = sourceAddrs + i * Libc.SockAddrInSize;
            recvVec[i].Header.NameLength = Libc.SockAddrInSize;
            recvVec[i].Header.Iov = &recvIov[i];
            recvVec[i].Header.IovLength = 1;
            if (gro)
            {
                recvVec[i].Header.Control = recvCtrl + i * CmsgSpace;
                recvVec[i].Header.ControlLength = CmsgSpace;
            }
        }
        cmsg->Length = 18; // CMSG_LEN(sizeof(u16))
        cmsg->Level = SOL_UDP;
        cmsg->Type = UDP_SEGMENT;
        sendIov->Base = packed;

        int* segSizes = stackalloc int[BatchSize];
        while (!ct.IsCancellationRequested)
        {
            for (int i = 0; i < BatchSize; i++)
            {
                recvVec[i].Header.NameLength = Libc.SockAddrInSize;
                if (gro)
                {
                    recvVec[i].Header.ControlLength = CmsgSpace;
                }
            }
            int received;
            if (plainRx)
            {
                nint one = Libc.RecvMsg(fd, &recvVec[0].Header, 0);
                if (one < 0)
                {
                    if (Marshal.GetLastWin32Error() == 4) continue;
                    throw new InvalidOperationException($"recvmsg failed: errno {Marshal.GetLastWin32Error()}");
                }
                recvVec[0].Length = (uint)one;
                received = 1;
            }
            else
            {
                received = Libc.RecvMmsg(fd, recvVec, BatchSize, Libc.MSG_WAITFORONE, null);
                if (received < 0)
                {
                    if (Marshal.GetLastWin32Error() == 4) continue;
                    throw new InvalidOperationException($"recvmmsg failed: errno {Marshal.GetLastWin32Error()}");
                }
            }

            // Effective segment size per message: the message length, unless a
            // UDP_GRO cmsg says the kernel coalesced several datagrams into it
            // (cmsghdr on 64-bit: u64 len, s32 level, s32 type, data at +16).
            for (int i = 0; i < received; i++)
            {
                int length = (int)recvVec[i].Length;
                int segment = length;
                if (gro && recvVec[i].Header.ControlLength >= 20)
                {
                    byte* c = recvCtrl + i * CmsgSpace;
                    if (*(nuint*)c >= 20 && *(int*)(c + 8) == SOL_UDP && *(int*)(c + 12) == UDP_GRO)
                    {
                        int fromCmsg = *(int*)(c + 16);
                        if (fromCmsg > 0) segment = fromCmsg;
                    }
                }
                segSizes[i] = segment;
                int remaining = length;
                while (remaining > 0)
                {
                    stats.PacketReceived(Math.Min(segment, remaining));
                    remaining -= segment;
                }
            }

            int start = 0;
            while (start < received)
            {
                int length = (int)recvVec[start].Length;
                int segment = segSizes[start];

                if (length > segment)
                {
                    // A coalesced blob is already a packed GSO batch: forward
                    // it straight from the receive slot, zero copies.
                    int blobSegments = (length + segment - 1) / segment;
                    sendIov->Base = data + start * SlotSize;
                    sendIov->Length = (nuint)length;
                    cmsg->GsoSize = (ushort)segment;
                    SendTo(fd, destinationCount, destAddrs, sendIov, cmsg, blobSegments, segment, stats);
                    start++;
                    continue;
                }

                // Single-datagram messages: pack runs of equal length, as before.
                int end = start + 1;
                while (end < received && segSizes[end] == segment && (int)recvVec[end].Length == segment)
                {
                    end++;
                }
                int count = end - start;
                nuint total = 0;
                for (int i = start; i < end; i++)
                {
                    Buffer.MemoryCopy(data + i * SlotSize, packed + total, SlotSize, (uint)segment);
                    total += (nuint)segment;
                }
                sendIov->Base = packed;
                sendIov->Length = total;
                cmsg->GsoSize = (ushort)segment;
                SendTo(fd, destinationCount, destAddrs, sendIov, cmsg, count, segment, stats);
                start = end;
            }
        }
    }

    private static void SendTo(
        int fd, int destinationCount, byte* destAddrs, Iovec* sendIov, CmsgSegment* cmsg,
        int count, int segment, ForwarderStats stats)
    {
        for (int d = 0; d < destinationCount; d++)
        {
            var header = new Msghdr
            {
                Name = destAddrs + d * Libc.SockAddrInSize,
                NameLength = Libc.SockAddrInSize,
                Iov = sendIov,
                IovLength = 1,
                Control = count > 1 ? cmsg : null, // single datagram: plain send
                ControlLength = count > 1 ? (nuint)24 : 0,
            };
            nint sent = SendMsg(fd, &header, 0);
            if (sent < 0)
            {
                if (Marshal.GetLastWin32Error() == 4) continue;
                throw new InvalidOperationException($"sendmsg(GSO) failed: errno {Marshal.GetLastWin32Error()}");
            }
            nint remaining = (nint)sendIov->Length;
            while (remaining > 0)
            {
                stats.PacketForwarded((int)Math.Min(segment, remaining));
                remaining -= segment;
            }
        }
    }
}
