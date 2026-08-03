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
/// </summary>
internal static unsafe partial class GsoEngine
{
    private const int BatchSize = 64;         // == UDP_MAX_SEGMENTS
    private const int SlotSize = 2048;
    private const int SOL_UDP = 17;
    private const int UDP_SEGMENT = 103;

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

    public static void Run(ForwarderOptions options, ForwarderStats stats, CancellationToken ct)
    {
        int fd = Libc.CreateBoundUdpSocket(options.ListenPort, 1 << 20);
        int destinationCount = options.Destinations.Count;

        byte* data = (byte*)NativeMemory.AllocZeroed(BatchSize * (nuint)SlotSize);
        byte* packed = (byte*)NativeMemory.AllocZeroed(BatchSize * (nuint)SlotSize);
        byte* sourceAddrs = (byte*)NativeMemory.AllocZeroed(BatchSize * (nuint)Libc.SockAddrInSize);
        byte* destAddrs = (byte*)NativeMemory.AllocZeroed((nuint)(destinationCount * Libc.SockAddrInSize));
        var recvIov = (Iovec*)NativeMemory.AllocZeroed((nuint)(BatchSize * sizeof(Iovec)));
        var recvVec = (Mmsghdr*)NativeMemory.AllocZeroed((nuint)(BatchSize * sizeof(Mmsghdr)));
        var cmsg = (CmsgSegment*)NativeMemory.AllocZeroed(24); // CMSG_SPACE(2)
        var sendIov = (Iovec*)NativeMemory.AllocZeroed((nuint)sizeof(Iovec));

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
        }
        cmsg->Length = 18; // CMSG_LEN(sizeof(u16))
        cmsg->Level = SOL_UDP;
        cmsg->Type = UDP_SEGMENT;
        sendIov->Base = packed;

        while (!ct.IsCancellationRequested)
        {
            for (int i = 0; i < BatchSize; i++)
            {
                recvVec[i].Header.NameLength = Libc.SockAddrInSize;
            }
            int received = Libc.RecvMmsg(fd, recvVec, BatchSize, Libc.MSG_WAITFORONE, null);
            if (received < 0)
            {
                if (Marshal.GetLastWin32Error() == 4) continue;
                throw new InvalidOperationException($"recvmmsg failed: errno {Marshal.GetLastWin32Error()}");
            }
            for (int i = 0; i < received; i++)
            {
                stats.PacketReceived((int)recvVec[i].Length);
            }

            // GSO wants equal-size segments (a smaller final one is allowed;
            // we keep it simple and split runs of equal length).
            int start = 0;
            while (start < received)
            {
                uint size = recvVec[start].Length;
                int end = start + 1;
                while (end < received && recvVec[end].Length == size)
                {
                    end++;
                }
                int count = end - start;

                nuint total = 0;
                for (int i = start; i < end; i++)
                {
                    Buffer.MemoryCopy(data + i * SlotSize, packed + total, SlotSize, size);
                    total += size;
                }
                sendIov->Length = total;
                cmsg->GsoSize = (ushort)size;

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
                    for (int i = 0; i < count; i++)
                    {
                        stats.PacketForwarded((int)size);
                    }
                }
                start = end;
            }
        }
    }
}
