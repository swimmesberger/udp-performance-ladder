using System.Runtime.InteropServices;
using Forwarder.Core;

namespace Forwarder.Rung3.Linux;

/// <summary>
/// The modest batching level: recvmmsg/sendmmsg, up to a whole batch of
/// datagrams per syscall, otherwise the same blocking serial loop as
/// rung 2. Send reuses the receive buffers directly (zero copy).
/// </summary>
internal static unsafe class MmsgEngine
{
    private const int BatchSize = 64;
    private const int SlotSize = 2048;

    public static void Run(ForwarderOptions options, ForwarderStats stats, CancellationToken ct)
    {
        int fd = Libc.CreateBoundUdpSocket(options.ListenPort, 1 << 20);
        int destinationCount = options.Destinations.Count;

        byte* data = (byte*)NativeMemory.AllocZeroed(BatchSize * (nuint)SlotSize);
        byte* sourceAddrs = (byte*)NativeMemory.AllocZeroed(BatchSize * (nuint)Libc.SockAddrInSize);
        byte* destAddrs = (byte*)NativeMemory.AllocZeroed((nuint)(destinationCount * Libc.SockAddrInSize));
        var recvIov = (Iovec*)NativeMemory.AllocZeroed((nuint)(BatchSize * sizeof(Iovec)));
        var sendIov = (Iovec*)NativeMemory.AllocZeroed((nuint)(BatchSize * sizeof(Iovec)));
        var recvVec = (Mmsghdr*)NativeMemory.AllocZeroed((nuint)(BatchSize * sizeof(Mmsghdr)));
        var sendVec = (Mmsghdr*)NativeMemory.AllocZeroed((nuint)(BatchSize * sizeof(Mmsghdr)));

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

            sendIov[i].Base = data + i * SlotSize; // same buffers, no copy
            sendVec[i].Header.NameLength = Libc.SockAddrInSize;
            sendVec[i].Header.Iov = &sendIov[i];
            sendVec[i].Header.IovLength = 1;
        }

        while (!ct.IsCancellationRequested)
        {
            // recvmmsg's name length is rewritten by the kernel per call.
            for (int i = 0; i < BatchSize; i++)
            {
                recvVec[i].Header.NameLength = Libc.SockAddrInSize;
            }

            int received = Libc.RecvMmsg(fd, recvVec, BatchSize, Libc.MSG_WAITFORONE, null);
            if (received < 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 4 /* EINTR */) continue;
                throw new InvalidOperationException($"recvmmsg failed: errno {error}");
            }

            for (int i = 0; i < received; i++)
            {
                stats.PacketReceived((int)recvVec[i].Length);
                sendIov[i].Length = recvVec[i].Length;
            }

            for (int d = 0; d < destinationCount; d++)
            {
                byte* dest = destAddrs + d * Libc.SockAddrInSize;
                for (int i = 0; i < received; i++)
                {
                    sendVec[i].Header.Name = dest;
                }

                int offset = 0;
                while (offset < received)
                {
                    int sent = Libc.SendMmsg(fd, sendVec + offset, (uint)(received - offset), 0);
                    if (sent < 0)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 4 /* EINTR */) continue;
                        throw new InvalidOperationException($"sendmmsg failed: errno {error}");
                    }
                    for (int i = 0; i < sent; i++)
                    {
                        stats.PacketForwarded((int)sendIov[offset + i].Length);
                    }
                    offset += sent;
                }
            }
        }
    }
}
