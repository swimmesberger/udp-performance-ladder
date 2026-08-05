using System.Net;
using System.Runtime.InteropServices;

namespace Forwarder.Rung3.Linux;

/// <summary>
/// Raw libc surface for the Linux engines. Sockets are created directly
/// via socket(2) so the engines own blocking behavior and buffer sizing
/// without the managed Socket's non-blocking emulation in the way.
/// </summary>
internal static unsafe partial class Libc
{
    public const int AF_INET = 2;
    public const int SOCK_DGRAM = 2;
    public const int SOL_SOCKET = 1;
    public const int SO_RCVBUF = 8;
    public const int MSG_WAITFORONE = 0x10000;
    public const int SockAddrInSize = 16;

    [LibraryImport("libc", EntryPoint = "socket", SetLastError = true)]
    public static partial int Socket(int domain, int type, int protocol);

    [LibraryImport("libc", EntryPoint = "bind", SetLastError = true)]
    public static partial int Bind(int fd, byte* addr, uint addrlen);

    [LibraryImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
    public static partial int SetSockOpt(int fd, int level, int optname, int* optval, uint optlen);

    [LibraryImport("libc", EntryPoint = "recvmmsg", SetLastError = true)]
    public static partial int RecvMmsg(int fd, Mmsghdr* msgvec, uint vlen, int flags, void* timeout);

    [LibraryImport("libc", EntryPoint = "recvmsg", SetLastError = true)]
    public static partial nint RecvMsg(int fd, Msghdr* msg, int flags);

    [LibraryImport("libc", EntryPoint = "sendmmsg", SetLastError = true)]
    public static partial int SendMmsg(int fd, Mmsghdr* msgvec, uint vlen, int flags);

    [LibraryImport("libc", EntryPoint = "mmap", SetLastError = true)]
    public static partial void* Mmap(void* addr, nuint length, int prot, int flags, int fd, long offset);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    public static partial long Syscall(long number, long a, long b, long c, long d, long e, long f);

    public static int CreateBoundUdpSocket(int listenPort, int receiveBufferBytes)
    {
        int fd = Socket(AF_INET, SOCK_DGRAM, 0);
        if (fd < 0)
        {
            throw new InvalidOperationException($"socket failed: errno {Marshal.GetLastWin32Error()}");
        }

        int size = receiveBufferBytes;
        if (SetSockOpt(fd, SOL_SOCKET, SO_RCVBUF, &size, sizeof(int)) != 0)
        {
            throw new InvalidOperationException($"SO_RCVBUF failed: errno {Marshal.GetLastWin32Error()}");
        }

        byte* addr = stackalloc byte[SockAddrInSize];
        WriteSockAddr(addr, new IPEndPoint(IPAddress.Any, listenPort));
        if (Bind(fd, addr, SockAddrInSize) != 0)
        {
            throw new InvalidOperationException($"bind failed: errno {Marshal.GetLastWin32Error()}");
        }
        return fd;
    }

    /// <summary>Writes a sockaddr_in (16 bytes) at <paramref name="destination"/>.</summary>
    public static void WriteSockAddr(byte* destination, IPEndPoint endpoint)
    {
        new Span<byte>(destination, SockAddrInSize).Clear();
        destination[0] = AF_INET;
        destination[1] = 0;
        destination[2] = (byte)(endpoint.Port >> 8);
        destination[3] = (byte)endpoint.Port;
        endpoint.Address.TryWriteBytes(new Span<byte>(destination + 4, 4), out _);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Iovec
{
    public void* Base;
    public nuint Length;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Msghdr
{
    public void* Name;
    public uint NameLength;
    public Iovec* Iov;
    public nuint IovLength;
    public void* Control;
    public nuint ControlLength;
    public int Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Mmsghdr
{
    public Msghdr Header;
    public uint Length;
}
