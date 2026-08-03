using System.Runtime.InteropServices;
using Forwarder.Core;

namespace Forwarder.Rung3.Linux;

/// <summary>
/// The ambitious batching level: io_uring, hand-rolled against the raw
/// syscalls (no liburing). Requests are written into a submission ring and
/// completions read from a completion ring, both mmap'd and shared with
/// the kernel; one io_uring_enter both submits the accumulated batch and
/// waits for completions. Same slot-rotation design as the Windows RIO
/// engine: posted receives are constant by construction, sends go straight
/// from the receive slot, and overload drops on our own counter.
/// </summary>
internal static unsafe class IoUringEngine
{
    // syscall numbers, x86_64
    private const long SysIoUringSetup = 425;
    private const long SysIoUringEnter = 426;

    private const uint EnterGetEvents = 1;         // IORING_ENTER_GETEVENTS
    private const byte OpSendMsg = 9;              // IORING_OP_SENDMSG
    private const byte OpRecvMsg = 10;             // IORING_OP_RECVMSG
    private const uint FeatSingleMmap = 1;         // IORING_FEAT_SINGLE_MMAP
    private const long OffSqRing = 0;
    private const long OffCqRing = 0x8000000;
    private const long OffSqes = 0x10000000;
    private const int ProtReadWrite = 3;
    private const int MapShared = 0x01;
    private const int MapPopulate = 0x8000;

    private const uint SqEntries = 1024;
    private const int PoolSlots = 768;
    private const int PostedReceives = 256;
    private const int SlotSize = 2048;
    private const ulong SendContextBit = 1UL << 63;

    public static void Run(ForwarderOptions options, ForwarderStats stats, CancellationToken ct)
    {
        int destinationCount = options.Destinations.Count;
        int socketFd = Libc.CreateBoundUdpSocket(options.ListenPort, 1 << 20);

        var setup = default(IoUringParams);
        long ringFd = Libc.Syscall(SysIoUringSetup, SqEntries, (long)&setup, 0, 0, 0, 0);
        if (ringFd < 0)
        {
            throw new InvalidOperationException(
                $"io_uring_setup failed: errno {Marshal.GetLastWin32Error()} " +
                "(a container seccomp profile may be blocking io_uring)");
        }

        // Map the rings. With IORING_FEAT_SINGLE_MMAP, sq and cq share one mapping.
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

        // Slot arena: data buffer, source address, recv msghdr + iovec, and
        // one send msghdr + iovec per destination, all preallocated.
        byte* data = (byte*)NativeMemory.AllocZeroed(PoolSlots * (nuint)SlotSize);
        byte* sourceAddrs = (byte*)NativeMemory.AllocZeroed(PoolSlots * (nuint)Libc.SockAddrInSize);
        byte* destAddrs = (byte*)NativeMemory.AllocZeroed((nuint)(destinationCount * Libc.SockAddrInSize));
        var recvIov = (Iovec*)NativeMemory.AllocZeroed((nuint)(PoolSlots * sizeof(Iovec)));
        var recvHdr = (Msghdr*)NativeMemory.AllocZeroed((nuint)(PoolSlots * sizeof(Msghdr)));
        var sendIov = (Iovec*)NativeMemory.AllocZeroed((nuint)(PoolSlots * destinationCount * sizeof(Iovec)));
        var sendHdr = (Msghdr*)NativeMemory.AllocZeroed((nuint)(PoolSlots * destinationCount * sizeof(Msghdr)));

        for (int d = 0; d < destinationCount; d++)
        {
            Libc.WriteSockAddr(destAddrs + d * Libc.SockAddrInSize, options.Destinations[d]);
        }
        for (int i = 0; i < PoolSlots; i++)
        {
            recvIov[i].Base = data + i * SlotSize;
            recvIov[i].Length = SlotSize;
            recvHdr[i].Name = sourceAddrs + i * Libc.SockAddrInSize;
            recvHdr[i].NameLength = Libc.SockAddrInSize;
            recvHdr[i].Iov = &recvIov[i];
            recvHdr[i].IovLength = 1;

            for (int d = 0; d < destinationCount; d++)
            {
                int s = i * destinationCount + d;
                sendIov[s].Base = data + i * SlotSize;
                sendHdr[s].Name = destAddrs + d * Libc.SockAddrInSize;
                sendHdr[s].NameLength = Libc.SockAddrInSize;
                sendHdr[s].Iov = &sendIov[s];
                sendHdr[s].IovLength = 1;
            }
        }

        var pendingSends = new int[PoolSlots];
        var freeSlots = new int[PoolSlots];
        int freeSlotCount = 0;
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
            recvHdr[slot].NameLength = Libc.SockAddrInSize; // kernel rewrites it
            recvIov[slot].Length = SlotSize;                // and the iov length
            sqArray[index] = index;
            localSqTail++;
            toSubmit++;
        }

        void PrepSend(int slot, uint length, int destination)
        {
            int s = slot * destinationCount + destination;
            sendIov[s].Length = length;
            uint index = localSqTail & sqMask;
            IoUringSqe* sqe = &sqes[index];
            *sqe = default;
            sqe->Opcode = OpSendMsg;
            sqe->Fd = socketFd;
            sqe->Addr = (ulong)&sendHdr[s];
            sqe->Len = 1;
            sqe->UserData = (ulong)(uint)slot | SendContextBit;
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

        for (int slot = 0; slot < PostedReceives; slot++)
        {
            PrepRecv(slot);
        }
        for (int slot = PostedReceives; slot < PoolSlots; slot++)
        {
            freeSlots[freeSlotCount++] = slot;
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
                    int slot = (int)(context & ~SendContextBit);
                    if (result >= 0)
                    {
                        stats.PacketForwarded(result);
                    }
                    if (--pendingSends[slot] == 0)
                    {
                        freeSlots[freeSlotCount++] = slot;
                    }
                }
                else
                {
                    int slot = (int)context;
                    if (result < 0)
                    {
                        PrepRecv(slot);
                    }
                    else
                    {
                        stats.PacketReceived(result);
                        if (freeSlotCount > 0)
                        {
                            PrepRecv(freeSlots[--freeSlotCount]);
                            pendingSends[slot] = destinationCount;
                            for (int d = 0; d < destinationCount; d++)
                            {
                                PrepSend(slot, (uint)result, d);
                            }
                        }
                        else
                        {
                            stats.PacketDropped();
                            PrepRecv(slot);
                        }
                    }
                }

                // Keep the submission ring from overflowing mid-drain.
                if (toSubmit > SqEntries - 8)
                {
                    Enter(minComplete: 0);
                }
            }
            System.Threading.Volatile.Write(ref *cqHead, head);
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

[StructLayout(LayoutKind.Sequential)]
internal struct IoSqringOffsets
{
    public uint Head, Tail, RingMask, RingEntries, Flags, Dropped, Array, Resv1;
    public ulong UserAddr;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IoCqringOffsets
{
    public uint Head, Tail, RingMask, RingEntries, Overflow, Cqes, Flags, Resv1;
    public ulong UserAddr;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IoUringParams
{
    public uint SqEntries, CqEntries, Flags, SqThreadCpu, SqThreadIdle, Features, WqFd;
    public uint Resv0, Resv1, Resv2;
    public IoSqringOffsets SqOff;
    public IoCqringOffsets CqOff;
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct IoUringSqe
{
    [FieldOffset(0)] public byte Opcode;
    [FieldOffset(1)] public byte Flags;
    [FieldOffset(2)] public ushort Ioprio;
    [FieldOffset(4)] public int Fd;
    [FieldOffset(8)] public ulong Off;
    [FieldOffset(16)] public ulong Addr;
    [FieldOffset(24)] public uint Len;
    [FieldOffset(28)] public uint MsgFlags;
    [FieldOffset(32)] public ulong UserData;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IoUringCqe
{
    public ulong UserData;
    public int Res;
    public uint Flags;
}
