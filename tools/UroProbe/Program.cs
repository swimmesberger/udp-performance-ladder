using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

// Definitive URO probe: raw WSARecvMsg with control-buffer space for
// UDP_COALESCED_INFO, msquic's exact receive shape. Reports datagrams per
// receive and coalescing cmsgs, the only reliable evidence that software
// URO is live. Exit code 0 = coalescing observed, 1 = none, so a script
// can bisect components with it.
//
// USE WIRE MODE. Loopback never coalesces even on a healthy stack:
// delivery there is synchronous per send, so no batch is ever available
// to merge, and a loopback run proves nothing about URO either way
// (measured 2026-08-05, mask 0, still zero coalescing).
//
//   UroProbe                                  loopback control
//   UroProbe wire <payloadSize> <backlogSecs> bind 0.0.0.0:5000 for
//                                             external traffic, sleeping
//                                             first so datagrams pile up
bool wire = args.Length > 0 && args[0] == "wire";
int Port = wire ? 5000 : 15920;
int PayloadSize = wire ? int.Parse(args[1]) : 1200;
int backlogSeconds = wire ? int.Parse(args[2]) : 0;
const int SendCount = 300_000;

using var rx = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
rx.Bind(new IPEndPoint(wire ? IPAddress.Any : IPAddress.Loopback, Port));
rx.ReceiveBufferSize = 1 << 22;
rx.ReceiveTimeout = 1500;
// msquic's exact value: MAX_URO_PAYLOAD_LENGTH = UINT16_MAX - UDP header
rx.SetSocketOption(SocketOptionLevel.Udp, (SocketOptionName)3 /* UDP_RECV_MAX_COALESCED_SIZE */, 65527);
Console.WriteLine($"URO opted in (UDP_RECV_MAX_COALESCED_SIZE = 65527), mode={(wire ? $"wire payload={PayloadSize} backlog={backlogSeconds}s" : "loopback")}");
if (backlogSeconds > 0)
{
    Thread.Sleep(backlogSeconds * 1000); // let the queue fill before the first receive
}

nint wsaRecvMsg = GetWsaRecvMsgPointer(rx.Handle);
Console.WriteLine($"WSARecvMsg pointer acquired: 0x{wsaRecvMsg:x}");

if (!wire)
{
    var sender = new Thread(() =>
    {
        using var tx = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tx.Connect(new IPEndPoint(IPAddress.Loopback, Port));
        byte[] payload = new byte[PayloadSize];
        for (int i = 0; i < SendCount; i++)
        {
            tx.Send(payload);
        }
    });
    sender.IsBackground = true;
    sender.Start();
}

long receives = 0, bytes = 0, coalescedCmsgs = 0, largest = 0, multiSegment = 0;
var histogram = new Dictionary<int, long>();

unsafe
{
    byte* data = (byte*)NativeMemory.AllocZeroed(128 * 1024);
    byte* control = (byte*)NativeMemory.AllocZeroed(512);
    byte* name = (byte*)NativeMemory.AllocZeroed(64);
    var buf = new WsaBuf { Length = 128 * 1024, Buffer = (nint)data };
    var deadline = DateTime.UtcNow.AddSeconds(12);

    var recvMsg = (delegate* unmanaged[Stdcall]<nint, WsaMsg*, uint*, nint, nint, int>)wsaRecvMsg;
    while (DateTime.UtcNow < deadline && receives + multiSegment < SendCount)
    {
        var msg = new WsaMsg
        {
            Name = (nint)name,
            NameLength = 64,
            Buffers = (nint)(&buf),
            BufferCount = 1,
            Control = new WsaBuf { Length = 512, Buffer = (nint)control },
            Flags = 0,
        };
        uint received = 0;
        int rc = recvMsg(rx.Handle, &msg, &received, 0, 0);
        if (rc != 0)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 10060 /* WSAETIMEDOUT */) break;
            if (err == 10054 /* WSAECONNRESET */) continue;
            Console.WriteLine($"WSARecvMsg failed: {err}");
            break;
        }
        receives++;
        bytes += received;
        if (received > largest) largest = received;
        histogram[(int)received] = histogram.GetValueOrDefault((int)received) + 1;
        if (received > PayloadSize) multiSegment += received / PayloadSize - 1;

        // Walk cmsgs: WSACMSGHDR x64 = { nuint len; int level; int type; data@16 }
        nuint controlLen = msg.Control.Length;
        byte* c = control;
        while (controlLen >= 16 && *(nuint*)c >= 16 && *(nuint*)c <= controlLen)
        {
            int level = *(int*)(c + 8);
            int type = *(int*)(c + 12);
            if (level == 17 /* IPPROTO_UDP */ && type == 3 /* UDP_COALESCED_INFO */)
            {
                coalescedCmsgs++;
            }
            nuint advance = (*(nuint*)c + 7) & ~(nuint)7;
            if (advance >= controlLen) break;
            controlLen -= advance;
            c += advance;
        }
    }
    NativeMemory.Free(data);
    NativeMemory.Free(control);
    NativeMemory.Free(name);
}

Console.WriteLine($"receives={receives:N0} bytes={bytes:N0} largest={largest:N0}");
Console.WriteLine($"coalesced cmsgs seen={coalescedCmsgs:N0}, extra segments via coalescing={multiSegment:N0}");
foreach (var (size, count) in histogram.OrderByDescending(kv => kv.Value).Take(6))
{
    Console.WriteLine($"  size {size,6}: {count:N0}");
}
Console.WriteLine(coalescedCmsgs > 0 ? "VERDICT: URO IS COALESCING" : "VERDICT: no coalescing");
Environment.ExitCode = coalescedCmsgs > 0 ? 0 : 1;

static unsafe nint GetWsaRecvMsgPointer(nint socket)
{
    var guid = new Guid(0xf689d7c8, 0x6f1f, 0x436b, 0x8a, 0x53, 0xe5, 0x4f, 0xe3, 0x51, 0xc3, 0x22);
    nint fn = 0;
    uint bytes = 0;
    int rc = WSAIoctl(socket, 0xC8000006 /* SIO_GET_EXTENSION_FUNCTION_POINTER */,
        &guid, (uint)sizeof(Guid), &fn, (uint)sizeof(nint), &bytes, 0, 0);
    if (rc != 0)
    {
        throw new InvalidOperationException($"WSAIoctl(WSARecvMsg) failed: {Marshal.GetLastWin32Error()}");
    }
    return fn;
}

[StructLayout(LayoutKind.Sequential)]
struct WsaBuf
{
    public uint Length;
    public nint Buffer;
}

[StructLayout(LayoutKind.Sequential)]
struct WsaMsg
{
    public nint Name;
    public int NameLength;
    public nint Buffers;
    public uint BufferCount;
    public WsaBuf Control;
    public uint Flags;
}

partial class Program
{
    [LibraryImport("ws2_32.dll", SetLastError = true)]
    internal static unsafe partial int WSAIoctl(
        nint socket, uint ioControlCode,
        Guid* inBuffer, uint inBufferSize,
        nint* outBuffer, uint outBufferSize,
        uint* bytesReturned, nint overlapped, nint completionRoutine);
}
