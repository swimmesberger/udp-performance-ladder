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
    private const int ReceiveSlots = 4096;
    private const int SlotSize = 2048;         // fits any non-jumbo datagram
    private const int AddrStride = 32;         // SOCKADDR_INET is 28, keep slots aligned
    private const int DequeueBatch = 256;
    private const ulong SendContextBit = 1UL << 63;

    private readonly ForwarderOptions _options;
    private readonly ForwarderStats _stats;

    private RioFunctionTable _rio;
    private IntPtr _socket;
    private IntPtr _completionQueue;
    private IntPtr _requestQueue;
    private IntPtr _bufferId;
    private IntPtr _event;
    private byte* _memory;
    private readonly int[] _pendingSends = new int[ReceiveSlots];

    private static int ReceiveDataOffset(int slot) => slot * SlotSize;
    private static int ReceiveAddrOffset(int slot) => ReceiveSlots * SlotSize + slot * AddrStride;
    private static int DestinationAddrOffset(int index) =>
        ReceiveSlots * SlotSize + ReceiveSlots * AddrStride + index * AddrStride;

    public RioForwarder(ForwarderOptions options, ForwarderStats stats)
    {
        _options = options;
        _stats = stats;
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

        byte* bindAddr = stackalloc byte[Rio.SockAddrInetSize];
        Rio.WriteSockAddr(bindAddr, new IPEndPoint(IPAddress.Any, _options.ListenPort));
        if (Rio.Bind(_socket, bindAddr, Rio.SockAddrInetSize) != 0)
        {
            throw new InvalidOperationException($"bind failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }

        _rio = Rio.LoadFunctionTable(_socket);

        int memorySize = ReceiveSlots * SlotSize
            + ReceiveSlots * AddrStride
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

        uint completionQueueSize = (uint)(ReceiveSlots + ReceiveSlots * destinationCount);
        _completionQueue = _rio.RIOCreateCompletionQueue(completionQueueSize, &notification);
        if (_completionQueue == IntPtr.Zero)
        {
            throw new InvalidOperationException($"RIOCreateCompletionQueue failed: {Rio.WSAGetLastError()}");
        }

        // args: socket, maxOutstandingReceive, maxReceiveDataBuffers,
        //       maxOutstandingSend, maxSendDataBuffers, receiveCQ, sendCQ, context
        _requestQueue = _rio.RIOCreateRequestQueue(
            _socket,
            ReceiveSlots, 1,
            (uint)(ReceiveSlots * destinationCount), 1,
            _completionQueue, _completionQueue, null);
        if (_requestQueue == IntPtr.Zero)
        {
            throw new InvalidOperationException($"RIOCreateRequestQueue failed: {Rio.WSAGetLastError()}");
        }

        for (int slot = 0; slot < ReceiveSlots; slot++)
        {
            PostReceive(slot);
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
                        PostReceive(slot);
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
                    _pendingSends[slot] = destinationCount;
                    for (int d = 0; d < destinationCount; d++)
                    {
                        PostSend(slot, result.BytesTransferred, d);
                    }
                }
            }
        }
    }

    private void PostReceive(int slot)
    {
        var data = new RioBuf
        {
            BufferId = _bufferId,
            Offset = (uint)ReceiveDataOffset(slot),
            Length = SlotSize,
        };
        var remote = new RioBuf
        {
            BufferId = _bufferId,
            Offset = (uint)ReceiveAddrOffset(slot),
            Length = Rio.SockAddrInetSize,
        };
        if (_rio.RIOReceiveEx(_requestQueue, &data, 1, null, &remote, null, null, 0, (void*)(nuint)slot) == 0)
        {
            throw new InvalidOperationException($"RIOReceiveEx failed: {Rio.WSAGetLastError()}");
        }
    }

    private void PostSend(int slot, uint length, int destination)
    {
        var data = new RioBuf
        {
            BufferId = _bufferId,
            Offset = (uint)ReceiveDataOffset(slot), // send straight from the receive slot
            Length = length,
        };
        var remote = new RioBuf
        {
            BufferId = _bufferId,
            Offset = (uint)DestinationAddrOffset(destination),
            Length = Rio.SockAddrInetSize,
        };
        ulong context = (uint)slot | SendContextBit;
        if (_rio.RIOSendEx(_requestQueue, &data, 1, null, &remote, null, null, 0, (void*)(nuint)context) == 0)
        {
            // Count the send as finished so the slot is not leaked.
            if (--_pendingSends[slot] == 0)
            {
                PostReceive(slot);
            }
        }
    }
}
