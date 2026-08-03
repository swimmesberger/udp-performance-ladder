using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace UdpBench;

/// <summary>
/// Batched send/receive loops for Linux via sendmmsg/recvmmsg: many
/// datagrams per syscall instead of one. This is what lets the generator
/// and sink outrun the forwarders they measure on modest hardware. The
/// managed Socket does setup (bind/connect/options); the data path talks
/// to its file descriptor directly. .NET keeps that fd non-blocking, so
/// EAGAIN is handled here rather than by kernel blocking.
/// </summary>
internal static unsafe partial class LinuxBatchIo
{
    private const int BatchSize = 64;
    private const int ReceiveSlotSize = 2048;
    private const int Eagain = 11;
    private const int Eintr = 4;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int sendmmsg(int sockfd, Mmsghdr* msgvec, uint vlen, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int recvmmsg(int sockfd, Mmsghdr* msgvec, uint vlen, int flags, void* timeout);

    public static void SendLoop(
        Socket socket,
        SendOptions options,
        int index,
        int threads,
        long[] counters,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        int fd = (int)socket.Handle;
        int size = options.Size;

        byte* data = (byte*)NativeMemory.AllocZeroed((nuint)(BatchSize * size));
        var iovecs = (Iovec*)NativeMemory.AllocZeroed((nuint)(BatchSize * sizeof(Iovec)));
        var headers = (Mmsghdr*)NativeMemory.AllocZeroed((nuint)(BatchSize * sizeof(Mmsghdr)));
        try
        {
            for (int i = 0; i < BatchSize; i++)
            {
                iovecs[i].Base = data + i * size;
                iovecs[i].Length = (nuint)size;
                headers[i].Header.Iov = &iovecs[i];
                headers[i].Header.IovLength = 1;
                // Name stays null: the socket is connected.
            }

            var duration = TimeSpan.FromSeconds(options.DurationSeconds);
            long sequence = index;
            long sent = 0;
            double threadRate = options.Rate > 0 ? (double)options.Rate / threads : 0;

            while (!ct.IsCancellationRequested
                   && (options.DurationSeconds == 0 || stopwatch.Elapsed < duration))
            {
                int batch = BatchSize;
                if (threadRate > 0)
                {
                    long due = (long)(stopwatch.Elapsed.TotalSeconds * threadRate);
                    long deficit = due - sent;
                    if (deficit <= 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }
                    batch = (int)Math.Min(BatchSize, deficit);
                }

                for (int i = 0; i < batch; i++)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(
                        new Span<byte>(data + i * size, 8), sequence + (long)i * threads);
                }

                int result = sendmmsg(fd, headers, (uint)batch, 0);
                if (result < 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error is Eagain or Eintr)
                    {
                        Thread.Sleep(1); // socket buffer full; let the NIC drain
                        continue;
                    }
                    throw new InvalidOperationException($"sendmmsg failed: errno {error}");
                }

                sent += result;
                sequence += (long)result * threads;
                Volatile.Write(ref counters[index], sent);
            }
        }
        finally
        {
            NativeMemory.Free(data);
            NativeMemory.Free(iovecs);
            NativeMemory.Free(headers);
            GC.KeepAlive(socket);
        }
    }

    public static void ReceiveLoop(Socket socket, ThreadCounters counters, Func<bool> running)
    {
        int fd = (int)socket.Handle;

        byte* data = (byte*)NativeMemory.AllocZeroed(BatchSize * (nuint)ReceiveSlotSize);
        var iovecs = (Iovec*)NativeMemory.AllocZeroed((nuint)(BatchSize * sizeof(Iovec)));
        var headers = (Mmsghdr*)NativeMemory.AllocZeroed((nuint)(BatchSize * sizeof(Mmsghdr)));
        try
        {
            for (int i = 0; i < BatchSize; i++)
            {
                iovecs[i].Base = data + i * ReceiveSlotSize;
                iovecs[i].Length = ReceiveSlotSize;
                headers[i].Header.Iov = &iovecs[i];
                headers[i].Header.IovLength = 1;
                // Name stays null: the sender's address is not needed.
            }

            while (running())
            {
                int result = recvmmsg(fd, headers, BatchSize, 0, null);
                if (result < 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error is Eagain or Eintr)
                    {
                        Thread.Sleep(1); // idle; also lets running() gate cancellation
                        continue;
                    }
                    throw new InvalidOperationException($"recvmmsg failed: errno {error}");
                }

                for (int i = 0; i < result; i++)
                {
                    int received = (int)headers[i].Length;
                    counters.Packets++;
                    counters.Bytes += received;
                    if (received >= 8)
                    {
                        long sequence = BinaryPrimitives.ReadInt64LittleEndian(
                            new ReadOnlySpan<byte>(data + i * ReceiveSlotSize, 8));
                        if (sequence < counters.MinSequence) counters.MinSequence = sequence;
                        if (sequence > counters.MaxSequence) counters.MaxSequence = sequence;
                    }
                }
            }
        }
        finally
        {
            NativeMemory.Free(data);
            NativeMemory.Free(iovecs);
            NativeMemory.Free(headers);
            GC.KeepAlive(socket);
        }
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
    public uint Length; // datagram bytes, filled by recvmmsg
}
