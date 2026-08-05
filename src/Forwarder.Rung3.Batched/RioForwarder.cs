using System.Net;
using System.Runtime.InteropServices;
using Forwarder.Core;

namespace Forwarder.Rung3;

/// <summary>
/// The rung 3 engine. Same serial pipeline as rungs 1 and 2 (receive, then
/// fan out, on one thread) and zero allocation in steady state; the single
/// changed variable is how work reaches the kernel. Requests are posted to
/// request queues and completions dequeued from a completion queue that both
/// live in memory shared with the kernel, so a packet no longer costs a
/// syscall on each side: RIODequeueCompletion is a user-mode read, and the
/// kernel is only signaled (RIONotify) when the queue runs dry.
/// </summary>
internal sealed unsafe class RioForwarder
{
    private const int PoolSlots = 12288;       // rotating: receive -> send -> free
    private const int PostedReceives = 4096;   // held constant by construction
    private const int SlotSize = 2048;         // fits any non-jumbo datagram
    private const int AddrStride = 32;         // SOCKADDR_INET is 28, keep slots aligned
    private const int DequeueBatch = 256;
    private const ulong SendContextBit = 1UL << 63;

    private readonly ForwarderOptions _options;
    private readonly ForwarderStats _stats;

    // Deferred mode: requests are inserted with RIO_MSG_DEFER (a pure
    // user-mode ring write) and the kernel is kicked once per dequeue batch
    // with RIO_MSG_COMMIT_ONLY, instead of once per posted request. This is
    // the submission-side twin of the batched completion harvest.
    private readonly bool _defer;
    private bool _receivesDeferred;
    private bool _sendsDeferred;

    private RioFunctionTable _rio;
    private IntPtr _socket;
    private IntPtr _completionQueue;
    private IntPtr _requestQueue;
    private IntPtr _bufferId;
    private IntPtr _event;
    private byte* _memory;

    // One pool; slots rotate roles: posted as a receive, then (holding data)
    // sent from directly, then returned to the free stack. Every receive
    // completion posts exactly one replacement receive, normally a free slot
    // so the just-filled slot can be sent from with zero copies. If the free
    // stack is empty (tx backpressure), the filled slot itself is reposted:
    // that one datagram is dropped on our own counter, and the posted-receive
    // count never shrinks, so RIO can never drop invisibly on an empty ring
    // (no OS counter records those).
    private readonly int[] _pendingSends = new int[PoolSlots];
    private readonly int[] _freeSlots = new int[PoolSlots];
    private int _freeSlotCount;

    private static int DataOffset(int slot) => slot * SlotSize;
    private static int AddrOffset(int slot) => PoolSlots * SlotSize + slot * AddrStride;
    private static int DestinationAddrOffset(int index) =>
        PoolSlots * SlotSize + PoolSlots * AddrStride + index * AddrStride;

    public RioForwarder(ForwarderOptions options, ForwarderStats stats, bool defer)
    {
        _options = options;
        _stats = stats;
        _defer = defer;
    }

    public void Run(CancellationToken ct)
    {
        int destinationCount = _options.Destinations.Count;
        Rio.Startup();

        _socket = Rio.WSASocketW(
            2 /* AF_INET */, 2 /* SOCK_DGRAM */, 17 /* IPPROTO_UDP */,
            IntPtr.Zero, 0, Rio.WsaFlagOverlapped | Rio.WsaFlagRegisteredIo);
        if (_socket == Rio.InvalidSocket)
        {
            throw new InvalidOperationException($"WSASocketW failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }

        // Aligned with every other rung (1 MB receive buffer). For RIO this
        // should be a no-op: posted receives in the registered buffer are the
        // buffering, and measurement confirmed identical numbers with and
        // without it. It stays so the alignment is uniform, not assumed.
        int receiveBuffer = 1 << 20;
        Rio.SetSockOpt(_socket, 0xFFFF /* SOL_SOCKET */, 0x1002 /* SO_RCVBUF */, &receiveBuffer, sizeof(int));

        byte* bindAddr = stackalloc byte[Rio.SockAddrInetSize];
        Rio.WriteSockAddr(bindAddr, new IPEndPoint(IPAddress.Any, _options.ListenPort));
        if (Rio.Bind(_socket, bindAddr, Rio.SockAddrInetSize) != 0)
        {
            throw new InvalidOperationException($"bind failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }

        _rio = Rio.LoadFunctionTable(_socket);

        int memorySize = PoolSlots * SlotSize
            + PoolSlots * AddrStride
            + destinationCount * AddrStride;
        _memory = (byte*)NativeMemory.AllocZeroed((nuint)memorySize);

        for (int i = 0; i < destinationCount; i++)
        {
            Rio.WriteSockAddr(_memory + DestinationAddrOffset(i), _options.Destinations[i]);
        }

        _bufferId = _rio.RIORegisterBuffer(_memory, (uint)memorySize);
        if (_bufferId == Rio.InvalidBufferId)
        {
            throw new InvalidOperationException($"RIORegisterBuffer failed: {Rio.WSAGetLastError()}");
        }

        _event = Rio.CreateEventW(IntPtr.Zero, manualReset: false, initialState: false, null);
        var notification = new RioNotificationCompletion
        {
            Type = Rio.EventCompletionType,
            EventHandle = _event,
            NotifyReset = 1,
        };

        uint completionQueueSize = (uint)(PostedReceives + (PoolSlots - PostedReceives) * destinationCount);
        _completionQueue = _rio.RIOCreateCompletionQueue(completionQueueSize, &notification);
        if (_completionQueue == IntPtr.Zero)
        {
            throw new InvalidOperationException($"RIOCreateCompletionQueue failed: {Rio.WSAGetLastError()}");
        }

        // args: socket, maxOutstandingReceive, maxReceiveDataBuffers,
        //       maxOutstandingSend, maxSendDataBuffers, receiveCQ, sendCQ, context
        _requestQueue = _rio.RIOCreateRequestQueue(
            _socket,
            PostedReceives, 1,
            (uint)((PoolSlots - PostedReceives) * destinationCount), 1,
            _completionQueue, _completionQueue, null);
        if (_requestQueue == IntPtr.Zero)
        {
            throw new InvalidOperationException($"RIOCreateRequestQueue failed: {Rio.WSAGetLastError()}");
        }

        for (int slot = 0; slot < PostedReceives; slot++)
        {
            PostReceive(slot);
        }
        if (_receivesDeferred)
        {
            CommitReceives();
        }
        for (int slot = PostedReceives; slot < PoolSlots; slot++)
        {
            _freeSlots[_freeSlotCount++] = slot;
        }

        var results = stackalloc RioResult[DequeueBatch];
        while (!ct.IsCancellationRequested)
        {
            uint count = _rio.RIODequeueCompletion(_completionQueue, results, DequeueBatch);
            if (count == Rio.CorruptCompletionQueue)
            {
                throw new InvalidOperationException("RIO completion queue corrupt");
            }

            if (count == 0)
            {
                // Queue is dry: arm the kernel notification, re-check for the
                // completion that may have landed in between, then sleep.
                _rio.RIONotify(_completionQueue);
                count = _rio.RIODequeueCompletion(_completionQueue, results, DequeueBatch);
                if (count == Rio.CorruptCompletionQueue)
                {
                    throw new InvalidOperationException("RIO completion queue corrupt");
                }
                if (count == 0)
                {
                    Rio.WaitForSingleObject(_event, 100);
                    continue;
                }
            }

            for (uint i = 0; i < count; i++)
            {
                ref RioResult result = ref results[i];
                if ((result.RequestContext & SendContextBit) != 0)
                {
                    int slot = (int)(result.RequestContext & ~SendContextBit);
                    if (result.Status == 0)
                    {
                        _stats.PacketForwarded((int)result.BytesTransferred);
                    }
                    if (--_pendingSends[slot] == 0)
                    {
                        _freeSlots[_freeSlotCount++] = slot;
                    }
                }
                else
                {
                    int slot = (int)result.RequestContext;
                    if (result.Status != 0)
                    {
                        PostReceive(slot); // e.g. a truncated datagram; recycle
                        continue;
                    }
                    _stats.PacketReceived((int)result.BytesTransferred);

                    if (_freeSlotCount > 0)
                    {
                        // Replacement receive comes from the free pool; the
                        // filled slot is sent from directly, zero copies.
                        PostReceive(_freeSlots[--_freeSlotCount]);
                        _pendingSends[slot] = destinationCount;
                        for (int d = 0; d < destinationCount; d++)
                        {
                            PostSend(slot, result.BytesTransferred, d);
                        }
                    }
                    else
                    {
                        // Tx backpressure: drop this datagram on our counter
                        // and recycle its slot as the replacement receive.
                        _stats.PacketDropped();
                        PostReceive(slot);
                    }
                }
            }

            // Deferred mode: one kernel kick per side per dequeue batch,
            // instead of one per posted request.
            if (_receivesDeferred)
            {
                CommitReceives();
            }
            if (_sendsDeferred)
            {
                CommitSends();
            }
        }
    }

    private void CommitReceives()
    {
        if (_rio.RIOReceiveEx(_requestQueue, null, 0, null, null, null, null, Rio.MsgCommitOnly, null) == 0)
        {
            throw new InvalidOperationException($"RIOReceiveEx(COMMIT_ONLY) failed: {Rio.WSAGetLastError()}");
        }
        _receivesDeferred = false;
    }

    private void CommitSends()
    {
        if (_rio.RIOSendEx(_requestQueue, null, 0, null, null, null, null, Rio.MsgCommitOnly, null) == 0)
        {
            throw new InvalidOperationException($"RIOSendEx(COMMIT_ONLY) failed: {Rio.WSAGetLastError()}");
        }
        _sendsDeferred = false;
    }

    private void PostReceive(int slot)
    {
        var data = new RioBuf
        {
            BufferId = _bufferId,
            Offset = (uint)DataOffset(slot),
            Length = SlotSize,
        };
        var remote = new RioBuf
        {
            BufferId = _bufferId,
            Offset = (uint)AddrOffset(slot),
            Length = Rio.SockAddrInetSize,
        };
        uint flags = 0;
        if (_defer)
        {
            flags = Rio.MsgDefer;
            _receivesDeferred = true;
        }
        if (_rio.RIOReceiveEx(_requestQueue, &data, 1, null, &remote, null, null, flags, (void*)(nuint)slot) == 0)
        {
            throw new InvalidOperationException($"RIOReceiveEx failed: {Rio.WSAGetLastError()}");
        }
    }

    private void PostSend(int slot, uint length, int destination)
    {
        var data = new RioBuf
        {
            BufferId = _bufferId,
            Offset = (uint)DataOffset(slot), // sent straight from the receive slot
            Length = length,
        };
        var remote = new RioBuf
        {
            BufferId = _bufferId,
            Offset = (uint)DestinationAddrOffset(destination),
            Length = Rio.SockAddrInetSize,
        };
        ulong context = (uint)slot | SendContextBit;
        uint flags = 0;
        if (_defer)
        {
            flags = Rio.MsgDefer;
            _sendsDeferred = true;
        }
        if (_rio.RIOSendEx(_requestQueue, &data, 1, null, &remote, null, null, flags, (void*)(nuint)context) == 0)
        {
            // Count the send as finished so the slot is not leaked.
            if (--_pendingSends[slot] == 0)
            {
                _freeSlots[_freeSlotCount++] = slot;
            }
        }
    }
}
