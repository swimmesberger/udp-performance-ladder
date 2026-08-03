using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Forwarder.Core;

namespace Forwarder.Rung3.Linux;

/// <summary>
/// The raw-frame half-step below XDP: AF_PACKET with memory-mapped rings
/// (TPACKET_V3 receive blocks, TPACKET_V2 transmit slots). No UDP socket
/// anywhere: a classic BPF filter selects our port's frames from the
/// interface, the engine parses Ethernet/IPv4/UDP itself, builds complete
/// reply frames itself, and syscalls happen once per block/batch (a poll
/// when idle, a kick per transmit batch). The price of the rung is that
/// everything the socket API did for us is now our code.
/// </summary>
internal static unsafe partial class AfPacketEngine
{
    private const int AF_PACKET = 17;
    private const int SOCK_RAW = 3;
    private const ushort ETH_P_IP = 0x0800;
    private const int SOL_PACKET = 263;
    private const int PACKET_VERSION = 10;
    private const int PACKET_RX_RING = 5;
    private const int PACKET_TX_RING = 13;
    private const int TPACKET_V2 = 1;
    private const int TPACKET_V3 = 2;
    private const int SO_ATTACH_FILTER = 26;
    private const uint TP_STATUS_USER = 1;
    private const uint TP_STATUS_SEND_REQUEST = 1;
    private const uint TP_STATUS_AVAILABLE = 0;
    private const short POLLIN = 1;

    private const uint BlockSize = 1 << 17;   // 128 KB, page multiple
    private const uint RxBlocks = 64;
    private const uint TxFrames = 4096;
    private const uint FrameSize = 2048;
    private const int TxDataOffset = 32;      // TPACKET_ALIGN(sizeof(tpacket2_hdr))
    private const int HeadersLength = 14 + 20 + 8; // eth + ipv4(no options) + udp

    [LibraryImport("libc", EntryPoint = "if_nametoindex", SetLastError = true)]
    private static partial uint IfNameToIndex([MarshalAs(UnmanagedType.LPStr)] string name);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int Poll(PollFd* fds, uint nfds, int timeout);

    [LibraryImport("libc", EntryPoint = "send", SetLastError = true)]
    private static partial nint Send(int fd, void* buf, nuint len, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { public int Fd; public short Events; public short Revents; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockFilter { public ushort Code; public byte Jt; public byte Jf; public uint K; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockFprog { public ushort Length; public SockFilter* Filter; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TpacketReq3
    {
        public uint BlockSize, BlockNr, FrameSize, FrameNr, RetireBlockTimeoutMs, SizeofPriv, FeatureReqWord;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TpacketReq { public uint BlockSize, BlockNr, FrameSize, FrameNr; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockaddrLl
    {
        public ushort Family;
        public ushort Protocol; // network byte order
        public int IfIndex;
        public ushort HaType;
        public byte PktType;
        public byte HaLen;
        public fixed byte Addr[8];
    }

    public static void Run(ForwarderOptions options, ForwarderStats stats, CancellationToken ct)
    {
        string interfaceName = Environment.GetEnvironmentVariable("AFPACKET_IFACE") ?? "lo";
        uint ifIndex = IfNameToIndex(interfaceName);
        if (ifIndex == 0)
        {
            throw new InvalidOperationException($"interface '{interfaceName}' not found");
        }
        int destinationCount = options.Destinations.Count;

        // A dummy UDP socket on the listen port, never read: without it the
        // kernel answers our port with ICMP unreachable and kills connected
        // senders. The stack still enqueues copies there (they overflow and
        // drop, harmlessly); that duplicated work is part of this rung's
        // honest price on a normal interface.
        int icmpSuppressor = Libc.CreateBoundUdpSocket(options.ListenPort, 1 << 14);
        _ = icmpSuppressor;

        // ---- RX: TPACKET_V3 block ring, BPF-filtered to our port ----
        int rxFd = Libc.Socket(AF_PACKET, SOCK_RAW, System.Net.IPAddress.HostToNetworkOrder((short)ETH_P_IP) & 0xFFFF);
        if (rxFd < 0) throw new InvalidOperationException($"packet socket failed: errno {Marshal.GetLastWin32Error()}");

        int version = TPACKET_V3;
        Check(Libc.SetSockOpt(rxFd, SOL_PACKET, PACKET_VERSION, &version, sizeof(int)), "PACKET_VERSION v3");
        AttachPortFilter(rxFd, (ushort)options.ListenPort);

        var rxReq = new TpacketReq3
        {
            BlockSize = BlockSize,
            BlockNr = RxBlocks,
            FrameSize = FrameSize,
            FrameNr = BlockSize / FrameSize * RxBlocks,
            RetireBlockTimeoutMs = 10,
        };
        Check(Libc.SetSockOpt(rxFd, SOL_PACKET, PACKET_RX_RING, (int*)&rxReq, (uint)sizeof(TpacketReq3)), "PACKET_RX_RING");
        byte* rxRing = MapRing(rxFd, BlockSize * RxBlocks);
        BindToInterface(rxFd, ifIndex, ETH_P_IP);

        // ---- TX: TPACKET_V2 frame ring on a second packet socket ----
        int txFd = Libc.Socket(AF_PACKET, SOCK_RAW, 0);
        if (txFd < 0) throw new InvalidOperationException($"tx packet socket failed: errno {Marshal.GetLastWin32Error()}");
        version = TPACKET_V2;
        Check(Libc.SetSockOpt(txFd, SOL_PACKET, PACKET_VERSION, &version, sizeof(int)), "PACKET_VERSION v2");
        var txReq = new TpacketReq
        {
            BlockSize = BlockSize,
            BlockNr = TxFrames * FrameSize / BlockSize,
            FrameSize = FrameSize,
            FrameNr = TxFrames,
        };
        Check(Libc.SetSockOpt(txFd, SOL_PACKET, PACKET_TX_RING, (int*)&txReq, (uint)sizeof(TpacketReq)), "PACKET_TX_RING");
        byte* txRing = MapRing(txFd, (nuint)TxFrames * FrameSize);
        BindToInterface(txFd, ifIndex, 0);

        // Destination header template fields.
        Span<byte> destIps = stackalloc byte[destinationCount * 4];
        Span<ushort> destPorts = stackalloc ushort[destinationCount];
        for (int d = 0; d < destinationCount; d++)
        {
            options.Destinations[d].Address.TryWriteBytes(destIps.Slice(d * 4, 4), out _);
            destPorts[d] = (ushort)options.Destinations[d].Port;
        }

        uint rxBlockIndex = 0;
        uint txFrameIndex = 0;
        var pollFd = new PollFd { Fd = rxFd, Events = POLLIN };

        while (!ct.IsCancellationRequested)
        {
            byte* block = rxRing + rxBlockIndex * BlockSize;
            uint blockStatus = System.Threading.Volatile.Read(ref *(uint*)(block + 8));
            if ((blockStatus & TP_STATUS_USER) == 0)
            {
                Poll(&pollFd, 1, 100);
                continue;
            }

            uint packetCount = *(uint*)(block + 12);
            uint offset = *(uint*)(block + 16);
            byte* packet = block + offset;
            int queued = 0;

            for (uint p = 0; p < packetCount; p++)
            {
                uint nextOffset = *(uint*)packet;          // tp_next_offset
                uint snapLength = *(uint*)(packet + 12);   // tp_snaplen
                ushort macOffset = *(ushort*)(packet + 24);
                byte* frame = packet + macOffset;

                // Loopback shows each datagram twice (outgoing + looped-back
                // copy); process only the inbound one. sockaddr_ll follows
                // tpacket3_hdr at +40; sll_pkttype is at +10 within it.
                if (packet[40 + 10] == 4 /* PACKET_OUTGOING */)
                {
                    packet += nextOffset;
                    continue;
                }

                // Parse: eth + IPv4 (variable IHL) + UDP; the BPF filter already
                // matched protocol and destination port.
                int ipHeaderLength = (frame[14] & 0x0F) * 4;
                byte* udp = frame + 14 + ipHeaderLength;
                int payloadLength = BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(udp + 4, 2)) - 8;
                byte* payload = udp + 8;
                if (payloadLength < 0 || (uint)(14 + ipHeaderLength + 8 + payloadLength) > snapLength)
                {
                    packet += nextOffset;
                    continue;
                }
                stats.PacketReceived(payloadLength);

                for (int d = 0; d < destinationCount; d++)
                {
                    byte* slot = txRing + txFrameIndex * FrameSize;
                    if (System.Threading.Volatile.Read(ref *(uint*)slot) != TP_STATUS_AVAILABLE)
                    {
                        stats.PacketDropped(); // tx ring full: counted, not hidden
                        continue;
                    }
                    byte* outFrame = slot + TxDataOffset;
                    BuildFrame(outFrame, destIps.Slice(d * 4, 4), destPorts[d],
                        (ushort)options.ListenPort, payload, payloadLength);
                    *(uint*)(slot + 4) = (uint)(HeadersLength + payloadLength); // tp_len
                    System.Threading.Volatile.Write(ref *(uint*)slot, TP_STATUS_SEND_REQUEST);
                    txFrameIndex = (txFrameIndex + 1) % TxFrames;
                    queued++;
                    stats.PacketForwarded(payloadLength);
                }
                packet += nextOffset;
            }

            if (queued > 0)
            {
                // One kick per block of frames. A negative return means the
                // kernel rejected the frames we built; without this check the
                // engine reports a perfect forwarding rate while delivering
                // nothing (it happened, and only the sink's counter caught it).
                if (Send(txFd, null, 0, 0) < 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 11 /* EAGAIN */)
                    {
                        throw new InvalidOperationException(
                            $"AF_PACKET tx kick failed: errno {error}");
                    }
                }
            }
            System.Threading.Volatile.Write(ref *(uint*)(block + 8), 0); // back to the kernel
            rxBlockIndex = (rxBlockIndex + 1) % RxBlocks;
        }
    }

    /// <summary>Complete Ethernet+IPv4+UDP frame; on loopback the MACs are zero.
    /// UDP checksum 0 is legal for IPv4; the IP header checksum is not optional.</summary>
    private static void BuildFrame(byte* frame, ReadOnlySpan<byte> destIp, ushort destPort,
        ushort sourcePort, byte* payload, int payloadLength)
    {
        var span = new Span<byte>(frame, HeadersLength + payloadLength);
        span[..14].Clear();
        BinaryPrimitives.WriteUInt16BigEndian(span[12..], ETH_P_IP);

        Span<byte> ip = span[14..];
        ip[0] = 0x45;
        ip[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)(20 + 8 + payloadLength));
        BinaryPrimitives.WriteUInt32BigEndian(ip[4..], 0);      // id + flags
        ip[8] = 64;                                             // ttl
        ip[9] = 17;                                             // udp
        BinaryPrimitives.WriteUInt16BigEndian(ip[10..], 0);     // checksum, below
        ip[12] = 127; ip[13] = 0; ip[14] = 0; ip[15] = 1;       // src 127.0.0.1
        destIp.CopyTo(ip[16..20]);
        BinaryPrimitives.WriteUInt16BigEndian(ip[10..], IpChecksum(ip[..20]));

        Span<byte> udp = span[34..];
        BinaryPrimitives.WriteUInt16BigEndian(udp, sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[2..], destPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[4..], (ushort)(8 + payloadLength));
        BinaryPrimitives.WriteUInt16BigEndian(udp[6..], 0);     // checksum optional in IPv4
        new ReadOnlySpan<byte>(payload, payloadLength).CopyTo(udp[8..]);
    }

    private static ushort IpChecksum(ReadOnlySpan<byte> header)
    {
        uint sum = 0;
        for (int i = 0; i < header.Length; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(header[i..]);
        }
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return (ushort)~sum;
    }

    /// <summary>Classic BPF: ip and udp and not fragmented and dst port N.</summary>
    private static void AttachPortFilter(int fd, ushort port)
    {
        SockFilter* f = stackalloc SockFilter[11];
        f[0] = new SockFilter { Code = 0x28, K = 12 };                    // ldh ethertype
        f[1] = new SockFilter { Code = 0x15, Jf = 8, K = ETH_P_IP };      // jeq ip
        f[2] = new SockFilter { Code = 0x30, K = 23 };                    // ldb ip proto
        f[3] = new SockFilter { Code = 0x15, Jf = 6, K = 17 };            // jeq udp
        f[4] = new SockFilter { Code = 0x28, K = 20 };                    // ldh frag
        f[5] = new SockFilter { Code = 0x45, Jt = 4, K = 0x1FFF };        // jset frag-offset
        f[6] = new SockFilter { Code = 0xB1, K = 14 };                    // ldx ip header len
        f[7] = new SockFilter { Code = 0x48, K = 16 };                    // ldh [x+16] dst port
        f[8] = new SockFilter { Code = 0x15, Jf = 1, K = port };          // jeq port
        f[9] = new SockFilter { Code = 0x06, K = 0x40000 };               // accept
        f[10] = new SockFilter { Code = 0x06, K = 0 };                    // drop
        var prog = new SockFprog { Length = 11, Filter = f };
        Check(Libc.SetSockOpt(fd, 1 /* SOL_SOCKET */, SO_ATTACH_FILTER, (int*)&prog, (uint)sizeof(SockFprog)), "SO_ATTACH_FILTER");
    }

    private static void BindToInterface(int fd, uint ifIndex, ushort protocol)
    {
        var addr = new SockaddrLl
        {
            Family = AF_PACKET,
            Protocol = (ushort)(System.Net.IPAddress.HostToNetworkOrder((short)protocol) & 0xFFFF),
            IfIndex = (int)ifIndex,
        };
        if (Libc.Bind(fd, (byte*)&addr, (uint)sizeof(SockaddrLl)) != 0)
        {
            throw new InvalidOperationException($"packet bind failed: errno {Marshal.GetLastWin32Error()}");
        }
    }

    private static byte* MapRing(int fd, nuint size)
    {
        void* mapped = Libc.Mmap(null, size, 3 /* rw */, 0x01 /* shared */ | 0x8000 /* populate */, fd, 0);
        if ((nint)mapped == -1)
        {
            throw new InvalidOperationException($"ring mmap failed: errno {Marshal.GetLastWin32Error()}");
        }
        return (byte*)mapped;
    }

    private static void Check(int result, string what)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"{what} failed: errno {Marshal.GetLastWin32Error()}");
        }
    }
}
