using System.Runtime.InteropServices;
using Forwarder.Core;

namespace Forwarder.Rung3.Linux;

/// <summary>
/// io_uring combined with the stack-batching family: receives and sends
/// travel through the submission/completion rings (one io_uring_enter per
/// drained batch), while the send side packs equal-size payloads and lets
/// the kernel segment them (UDP_SEGMENT), and the receive side can opt
/// into UDP_GRO coalescing. This is the shape msquic's Linux io_uring
/// datapath uses, made possible by the kernel allowing UDP cmsgs through
/// io_uring's sendmsg/recvmsg (PROTO_CMSG_DATA_ONLY). Receive slots repost
/// immediately (the pack copies the payload), so the posted-receive count
/// is constant by construction and overload drops on our own counter when
/// the pack pool runs dry.
/// </summary>
internal static unsafe class UringGsoEngine
{
    private const long SysIoUringSetup = 425;
    private const long SysIoUringEnter = 426;
    private const uint EnterGetEvents = 1;
    private const byte OpSendMsg = 9;
    private const byte OpRecvMsg = 10;
    private const uint FeatSingleMmap = 1;
    private const long OffSqRing = 0;
    private const long OffCqRing = 0x8000000;
    private const long OffSqes = 0x10000000;
    private const int ProtReadWrite = 3;
    private const int MapShared = 0x01;
    private const int MapPopulate = 0x8000;

    private const uint SqEntries = 1024;
    private const int PostedReceives = 256;
    private const int PackSlots = 64;
    private const int MaxSegments = 64;       // == UDP_MAX_SEGMENTS
    private const int SlotSize = 2048;
    private const int PackBytes = MaxSegments * SlotSize;
    private const int CmsgSpace = 24;         // CMSG_SPACE(4) on 64-bit
    private const int SOL_UDP = 17;
    private const int UDP_SEGMENT = 103;
    private const int UDP_GRO = 104;
    private const ulong SendContextBit = 1UL << 63;

    [StructLayout(LayoutKind.Sequential)]
    private struct CmsgSegment
    {
        public nuint Length;   // CMSG_LEN(2) = 18
        public int Level;
        public int Type;
        public ushort GsoSize;
    }

    public static void Run(ForwarderOptions options, ForwarderStats stats, bool gro, CancellationToken ct)
    {
        int destinationCount = options.Destinations.Count;
        int socketFd = Libc.CreateBoundUdpSocket(options.ListenPort, 1 << 20);
        if (gro)
        {
            int one = 1;
            if (Libc.SetSockOpt(socketFd, SOL_UDP, UDP_GRO, &one, sizeof(int)) != 0)
            {
                throw new InvalidOperationException($"setsockopt(UDP_GRO) failed: errno {Marshal.GetLastWin32Error()}");
            }
        }

        var setup = default(IoUringParams);
        long ringFd = Libc.Syscall(SysIoUringSetup, SqEntries, (long)&setup, 0, 0, 0, 0);
        if (ringFd < 0)
        {
            throw new InvalidOperationException(
                $"io_uring_setup failed: errno {Marshal.GetLastWin32Error()} " +
                "(a container seccomp profile may be blocking io_uring)");
        }

        nuint sqSize = setup.SqOff.Array + setup.SqEntries * sizeof(uint);
        nuint cqSize = setup.CqOff.Cqes + setup.CqEntries * (nuint)sizeof(IoUringCqe);
        bool single = (setup.Features & FeatSingleMmap) != 0;
        if (single && cqSize > sqSize)
        {
            sqSize = cqSize;
        }
        byte* sqBase = MapRing((int)ringFd, sqSize, OffSqRing);
        byte* cqBase = single ? sqBase : MapRing((int)ringFd, cqSize, OffCqRing);
        var sqes = (IoUringSqe*)MapRing((int)ringFd, setup.SqEntries * (nuint)sizeof(IoUringSqe), OffSqes);

        uint* sqTail = (uint*)(sqBase + setup.SqOff.Tail);
        uint sqMask = *(uint*)(sqBase + setup.SqOff.RingMask);
        uint* sqArray = (uint*)(sqBase + setup.SqOff.Array);
        uint* cqHead = (uint*)(cqBase + setup.CqOff.Head);
        uint* cqTail = (uint*)(cqBase + setup.CqOff.Tail);
        uint cqMask = *(uint*)(cqBase + setup.CqOff.RingMask);
        var cqes = (IoUringCqe*)(cqBase + setup.CqOff.Cqes);

        // Receive arena: data, source address, msghdr + iovec + GRO control
        // per posted receive. Pack arena: one big buffer, msghdr + iovec +
        // UDP_SEGMENT cmsg per pack slot per destination.
        byte* data = (byte*)NativeMemory.AllocZeroed(PostedReceives * (nuint)SlotSize);
        byte* sourceAddrs = (byte*)NativeMemory.AllocZeroed(PostedReceives * (nuint)Libc.SockAddrInSize);
        byte* recvCtrl = (byte*)NativeMemory.AllocZeroed(PostedReceives * (nuint)CmsgSpace);
        var recvIov = (Iovec*)NativeMemory.AllocZeroed((nuint)(PostedReceives * sizeof(Iovec)));
        var recvHdr = (Msghdr*)NativeMemory.AllocZeroed((nuint)(PostedReceives * sizeof(Msghdr)));

        byte* destAddrs = (byte*)NativeMemory.AllocZeroed((nuint)(destinationCount * Libc.SockAddrInSize));
        byte* pack = (byte*)NativeMemory.AllocZeroed((nuint)(PackSlots * PackBytes));
        var packIov = (Iovec*)NativeMemory.AllocZeroed((nuint)(PackSlots * destinationCount * sizeof(Iovec)));
        var packHdr = (Msghdr*)NativeMemory.AllocZeroed((nuint)(PackSlots * destinationCount * sizeof(Msghdr)));
        var packCmsg = (CmsgSegment*)NativeMemory.AllocZeroed((nuint)(PackSlots * destinationCount * CmsgSpace));

        for (int d = 0; d < destinationCount; d++)
        {
            Libc.WriteSockAddr(destAddrs + d * Libc.SockAddrInSize, options.Destinations[d]);
        }
        for (int i = 0; i < PostedReceives; i++)
        {
            recvIov[i].Base = data + i * SlotSize;
            recvIov[i].Length = SlotSize;
            recvHdr[i].Name = sourceAddrs + i * Libc.SockAddrInSize;
            recvHdr[i].NameLength = Libc.SockAddrInSize;
            recvHdr[i].Iov = &recvIov[i];
            recvHdr[i].IovLength = 1;
            if (gro)
            {
                recvHdr[i].Control = recvCtrl + i * CmsgSpace;
                recvHdr[i].ControlLength = CmsgSpace;
            }
        }
        for (int p = 0; p < PackSlots; p++)
        {
            for (int d = 0; d < destinationCount; d++)
            {
                int s = p * destinationCount + d;
                packIov[s].Base = pack + p * PackBytes;
                packHdr[s].Name = destAddrs + d * Libc.SockAddrInSize;
                packHdr[s].NameLength = Libc.SockAddrInSize;
                packHdr[s].Iov = &packIov[s];
                packHdr[s].IovLength = 1;
                var c = &packCmsg[s];
                c->Length = 18; // CMSG_LEN(sizeof(u16))
                c->Level = SOL_UDP;
                c->Type = UDP_SEGMENT;
            }
        }

        var packPendingSends = new int[PackSlots];
        var packSegmentCount = new int[PackSlots];
        var packSegmentSize = new int[PackSlots];
        var freePacks = new int[PackSlots];
        int freePackCount = 0;
        for (int p = 0; p < PackSlots; p++)
        {
            freePacks[freePackCount++] = p;
        }

        uint localSqTail = *sqTail;
        uint toSubmit = 0;

        void PrepRecv(int slot)
        {
            uint index = localSqTail & sqMask;
            IoUringSqe* sqe = &sqes[index];
            *sqe = default;
            sqe->Opcode = OpRecvMsg;
            sqe->Fd = socketFd;
            sqe->Addr = (ulong)&recvHdr[slot];
            sqe->Len = 1;
            sqe->UserData = (ulong)slot;
            recvHdr[slot].NameLength = Libc.SockAddrInSize;
            recvIov[slot].Length = SlotSize;
            if (gro)
            {
                recvHdr[slot].ControlLength = CmsgSpace;
            }
            sqArray[index] = index;
            localSqTail++;
            toSubmit++;
        }

        int Enter(uint minComplete)
        {
            System.Threading.Volatile.Write(ref *sqTail, localSqTail);
            long result = Libc.Syscall(
                SysIoUringEnter, ringFd, toSubmit, minComplete, EnterGetEvents, 0, 0);
            if (result < 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 4 /* EINTR */) return 0;
                throw new InvalidOperationException($"io_uring_enter failed: errno {error}");
            }
            toSubmit = 0;
            return (int)result;
        }

        // The pack being filled: -1 segment size means empty.
        int currentPack = -1;
        int currentBytes = 0;
        int currentSegments = 0;
        int currentSegSize = -1;

        void FlushPack()
        {
            if (currentPack < 0 || currentSegments == 0)
            {
                return;
            }
            packPendingSends[currentPack] = destinationCount;
            packSegmentCount[currentPack] = currentSegments;
            packSegmentSize[currentPack] = currentSegSize;
            for (int d = 0; d < destinationCount; d++)
            {
                int s = currentPack * destinationCount + d;
                packIov[s].Length = (nuint)currentBytes;
                packCmsg[s].GsoSize = (ushort)currentSegSize;
                bool segmented = currentSegments > 1;
                packHdr[s].Control = segmented ? &packCmsg[s] : null;
                packHdr[s].ControlLength = segmented ? (nuint)CmsgSpace : 0;

                uint index = localSqTail & sqMask;
                IoUringSqe* sqe = &sqes[index];
                *sqe = default;
                sqe->Opcode = OpSendMsg;
                sqe->Fd = socketFd;
                sqe->Addr = (ulong)&packHdr[s];
                sqe->Len = 1;
                sqe->UserData = (ulong)(uint)currentPack | SendContextBit;
                sqArray[index] = index;
                localSqTail++;
                toSubmit++;
            }
            currentPack = -1;
            currentSegments = 0;
            currentBytes = 0;
            currentSegSize = -1;
        }

        for (int slot = 0; slot < PostedReceives; slot++)
        {
            PrepRecv(slot);
        }

        while (!ct.IsCancellationRequested)
        {
            Enter(minComplete: 1);

            uint head = *cqHead;
            uint tail = System.Threading.Volatile.Read(ref *cqTail);
            while (head != tail)
            {
                IoUringCqe* cqe = &cqes[head & cqMask];
                ulong context = cqe->UserData;
                int result = cqe->Res;
                head++;

                if ((context & SendContextBit) != 0)
                {
                    int p = (int)(context & ~SendContextBit);
                    int segment = packSegmentSize[p];
                    if (result >= 0)
                    {
                        int remaining = result;
                        while (remaining > 0)
                        {
                            stats.PacketForwarded(Math.Min(segment, remaining));
                            remaining -= segment;
                        }
                    }
                    else
                    {
                        for (int k = 0; k < packSegmentCount[p]; k++)
                        {
                            stats.PacketDropped();
                        }
                    }
                    if (--packPendingSends[p] == 0)
                    {
                        freePacks[freePackCount++] = p;
                    }
                }
                else
                {
                    int slot = (int)context;
                    if (result > 0)
                    {
                        int segment = result;
                        if (gro && recvHdr[slot].ControlLength >= 20)
                        {
                            byte* c = recvCtrl + slot * CmsgSpace;
                            if (*(nuint*)c >= 20 && *(int*)(c + 8) == SOL_UDP && *(int*)(c + 12) == UDP_GRO)
                            {
                                int fromCmsg = *(int*)(c + 16);
                                if (fromCmsg > 0) segment = fromCmsg;
                            }
                        }
                        int remaining = result;
                        while (remaining > 0)
                        {
                            stats.PacketReceived(Math.Min(segment, remaining));
                            remaining -= segment;
                        }
                        int blobSegments = (result + segment - 1) / segment;
                        bool tailEndsRun = result % segment != 0;

                        // Append to the pack being filled; a segment-size
                        // change, a full pack, or a blob with a short tail
                        // flushes it.
                        if (currentPack >= 0 &&
                            (segment != currentSegSize || currentSegments + blobSegments > MaxSegments))
                        {
                            FlushPack();
                        }
                        if (currentPack < 0)
                        {
                            if (freePackCount == 0)
                            {
                                for (int k = 0; k < blobSegments; k++)
                                {
                                    stats.PacketDropped();
                                }
                                PrepRecv(slot);
                                goto submitted;
                            }
                            currentPack = freePacks[--freePackCount];
                            currentSegSize = segment;
                        }
                        Buffer.MemoryCopy(
                            data + slot * SlotSize,
                            pack + currentPack * PackBytes + currentBytes,
                            PackBytes - currentBytes,
                            result);
                        currentBytes += result;
                        currentSegments += blobSegments;
                        if (tailEndsRun || currentSegments >= MaxSegments)
                        {
                            FlushPack();
                        }
                    }
                    PrepRecv(slot);
                }

            submitted:
                if (toSubmit > SqEntries - 8)
                {
                    FlushPack();
                    Enter(minComplete: 0);
                }
            }
            System.Threading.Volatile.Write(ref *cqHead, head);
            FlushPack();
        }
    }

    private static byte* MapRing(int ringFd, nuint size, long offset)
    {
        void* mapped = Libc.Mmap(null, size, ProtReadWrite, MapShared | MapPopulate, ringFd, offset);
        if ((nint)mapped == -1)
        {
            throw new InvalidOperationException($"mmap failed: errno {Marshal.GetLastWin32Error()}");
        }
        return (byte*)mapped;
    }
}
