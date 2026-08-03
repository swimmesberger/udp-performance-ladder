using System.Runtime.InteropServices;

namespace Forwarder.Rung3;

/// <summary>
/// Interop surface for Windows Registered I/O (RIO). .NET does not expose
/// RIO, so everything here is hand-written against mswsock.h. The function
/// table is fetched at runtime via WSAIoctl; the structs must match the
/// native layouts exactly.
/// </summary>
internal static unsafe partial class Rio
{
    public const uint WsaFlagOverlapped = 0x01;
    public const uint WsaFlagRegisteredIo = 0x100;
    public const uint SioGetMultipleExtensionFunctionPointer = 0xC8000024;
    public const uint CorruptCompletionQueue = 0xFFFFFFFF;
    public const int EventCompletionType = 1;
    public const int SockAddrInetSize = 28;

    public static readonly IntPtr InvalidSocket = (IntPtr)(-1);
    public static readonly IntPtr InvalidBufferId = unchecked((IntPtr)(nint)uint.MaxValue);

    private static Guid MultipleRioGuid = new(
        0x8509e081, 0x96dd, 0x4005, 0xb1, 0x65, 0x9e, 0x2e, 0xe8, 0xc7, 0x9e, 0x3f);

    [LibraryImport("ws2_32.dll", SetLastError = true)]
    private static partial int WSAStartup(ushort versionRequested, byte* wsaData);

    [LibraryImport("ws2_32.dll", SetLastError = true)]
    public static partial IntPtr WSASocketW(
        int addressFamily, int socketType, int protocol,
        IntPtr protocolInfo, uint group, uint flags);

    [LibraryImport("ws2_32.dll", SetLastError = true, EntryPoint = "bind")]
    public static partial int Bind(IntPtr socket, byte* name, int nameLength);

    [LibraryImport("ws2_32.dll", SetLastError = true, EntryPoint = "closesocket")]
    public static partial int CloseSocket(IntPtr socket);

    [LibraryImport("ws2_32.dll", SetLastError = true)]
    private static partial int WSAIoctl(
        IntPtr socket, uint ioControlCode,
        Guid* inBuffer, uint inBufferSize,
        void* outBuffer, uint outBufferSize,
        uint* bytesReturned, IntPtr overlapped, IntPtr completionRoutine);

    [LibraryImport("ws2_32.dll")]
    public static partial int WSAGetLastError();

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateEventW(
        IntPtr attributes,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState,
        string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    public static void Startup()
    {
        byte* data = stackalloc byte[512];
        int error = WSAStartup(0x0202, data);
        if (error != 0)
        {
            throw new InvalidOperationException($"WSAStartup failed: {error}");
        }
    }

    public static RioFunctionTable LoadFunctionTable(IntPtr socket)
    {
        RioFunctionTable table = default;
        uint bytes = 0;
        fixed (Guid* guid = &MultipleRioGuid)
        {
            int result = WSAIoctl(
                socket, SioGetMultipleExtensionFunctionPointer,
                guid, (uint)sizeof(Guid),
                &table, (uint)sizeof(RioFunctionTable),
                &bytes, IntPtr.Zero, IntPtr.Zero);
            if (result != 0)
            {
                throw new InvalidOperationException(
                    $"WSAIoctl(SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER) failed: {WSAGetLastError()}");
            }
        }
        return table;
    }

    /// <summary>Writes an IPv4 SOCKADDR_INET into 28 bytes at <paramref name="destination"/>.</summary>
    public static void WriteSockAddr(byte* destination, System.Net.IPEndPoint endpoint)
    {
        new Span<byte>(destination, SockAddrInetSize).Clear();
        destination[0] = 2; // AF_INET, little-endian short
        destination[1] = 0;
        destination[2] = (byte)(endpoint.Port >> 8); // port in network order
        destination[3] = (byte)endpoint.Port;
        endpoint.Address.TryWriteBytes(new Span<byte>(destination + 4, 4), out _);
    }
}

/// <summary>RIO_BUF: a slice of a registered buffer.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RioBuf
{
    public IntPtr BufferId;
    public uint Offset;
    public uint Length;
}

/// <summary>RIORESULT: one dequeued completion.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RioResult
{
    public int Status;
    public uint BytesTransferred;
    public ulong SocketContext;
    public ulong RequestContext;
}

/// <summary>RIO_NOTIFICATION_COMPLETION, event flavor of the union.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RioNotificationCompletion
{
    public int Type;
    public IntPtr EventHandle;
    public int NotifyReset;
    private IntPtr _iocpUnionPadding; // the IOCP branch of the union is larger
}

/// <summary>
/// RIO_EXTENSION_FUNCTION_TABLE. Field order must match mswsock.h exactly;
/// these are raw function pointers fetched from the kernel-mode provider.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RioFunctionTable
{
    public uint Size;

    public delegate* unmanaged[Stdcall]<IntPtr, RioBuf*, uint, uint, void*, int> Receive;
    public delegate* unmanaged[Stdcall]<
        IntPtr, RioBuf*, uint, RioBuf*, RioBuf*, RioBuf*, RioBuf*, uint, void*, int> ReceiveEx;
    public delegate* unmanaged[Stdcall]<IntPtr, RioBuf*, uint, uint, void*, int> Send;
    public delegate* unmanaged[Stdcall]<
        IntPtr, RioBuf*, uint, RioBuf*, RioBuf*, RioBuf*, RioBuf*, uint, void*, int> SendEx;
    public delegate* unmanaged[Stdcall]<IntPtr, void> CloseCompletionQueue;
    public delegate* unmanaged[Stdcall]<uint, RioNotificationCompletion*, IntPtr> CreateCompletionQueue;
    public delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, IntPtr, IntPtr, void*, IntPtr> CreateRequestQueue;
    public delegate* unmanaged[Stdcall]<IntPtr, RioResult*, uint, uint> DequeueCompletion;
    public delegate* unmanaged[Stdcall]<IntPtr, void> DeregisterBuffer;
    public delegate* unmanaged[Stdcall]<IntPtr, int> Notify;
    public delegate* unmanaged[Stdcall]<byte*, uint, IntPtr> RegisterBuffer;
    public delegate* unmanaged[Stdcall]<IntPtr, uint, int> ResizeCompletionQueue;
    public delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int> ResizeRequestQueue;
}
