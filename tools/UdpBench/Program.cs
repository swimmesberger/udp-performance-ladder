// udpbench: UDP load generator and measuring sink in one binary.
//
// Every datagram carries a 64-bit little-endian sequence number in its
// first 8 bytes, so a sink can derive loss from the gap between the
// sequence span it observed and the packet count it received, without
// any control channel to the sender. The loss calculation assumes a
// single sender per sink.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

return args[0] switch
{
    "send" => RunSend(args[1..]),
    "sink" => RunSink(args[1..]),
    _ => PrintUsage(),
};

static int PrintUsage()
{
    Console.Error.WriteLine(
        """
        usage:
          udpbench send --target <host:port> [--size <bytes>] [--rate <pps>] [--duration <seconds>]
          udpbench sink --listen <port>

        send options:
          --size      UDP payload bytes, minimum 8 (default 32)
          --rate      packets per second; 0 = unthrottled (default 0)
          --duration  seconds to run; 0 = until Ctrl+C (default 10)
        """);
    return 1;
}

static int RunSend(string[] args)
{
    string? target = null;
    int size = 32;
    long rate = 0;
    int durationSeconds = 10;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--target":
                target = args[++i];
                break;
            case "--size":
                size = int.Parse(args[++i]);
                break;
            case "--rate":
                rate = long.Parse(args[++i]);
                break;
            case "--duration":
                durationSeconds = int.Parse(args[++i]);
                break;
            default:
                Console.Error.WriteLine($"unknown argument '{args[i]}'");
                return PrintUsage();
        }
    }

    if (target is null)
    {
        Console.Error.WriteLine("--target is required");
        return PrintUsage();
    }
    if (size < 8)
    {
        Console.Error.WriteLine("--size must be at least 8 (sequence number header)");
        return 1;
    }

    IPEndPoint endpoint = ResolveEndPoint(target);
    using var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
    socket.SendBufferSize = 4 << 20;
    socket.Connect(endpoint);

    bool stop = false;
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop = true;
    };

    byte[] payload = new byte[size];
    var duration = TimeSpan.FromSeconds(durationSeconds);
    var stopwatch = Stopwatch.StartNew();
    long sent = 0;
    long sentAtLastReport = 0;
    long nextReportMs = 1000;

    Console.WriteLine(
        $"sending to {endpoint}, payload {size} B, " +
        $"rate {(rate == 0 ? "unthrottled" : $"{rate:N0} pps")}, " +
        $"duration {(durationSeconds == 0 ? "until Ctrl+C" : $"{durationSeconds} s")}");

    while (!stop && (durationSeconds == 0 || stopwatch.Elapsed < duration))
    {
        if (rate > 0)
        {
            long due = (long)(stopwatch.Elapsed.TotalSeconds * rate);
            if (sent >= due)
            {
                Thread.SpinWait(64);
                continue;
            }
        }

        BinaryPrimitives.WriteInt64LittleEndian(payload, sent);
        socket.Send(payload);
        sent++;

        if (stopwatch.ElapsedMilliseconds >= nextReportMs)
        {
            long delta = sent - sentAtLastReport;
            Console.WriteLine($"tx {delta,11:N0} pps {delta * size * 8 / 1_000_000.0,8:N1} Mbit/s");
            sentAtLastReport = sent;
            nextReportMs += 1000;
        }
    }

    double seconds = stopwatch.Elapsed.TotalSeconds;
    Console.WriteLine(
        $"done: {sent:N0} packets in {seconds:N1} s " +
        $"({sent / seconds:N0} pps, {sent * size * 8 / seconds / 1_000_000:N1} Mbit/s payload)");
    return 0;
}

static int RunSink(string[] args)
{
    int port = 6000;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--listen":
                port = int.Parse(args[++i]);
                break;
            default:
                Console.Error.WriteLine($"unknown argument '{args[i]}'");
                return PrintUsage();
        }
    }

    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    socket.ReceiveBufferSize = 8 << 20;
    socket.Bind(new IPEndPoint(IPAddress.Any, port));

    long packets = 0;
    long bytes = 0;
    long minSequence = long.MaxValue;
    long maxSequence = -1;
    bool stop = false;

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop = true;
        socket.Close(); // unblocks the Receive call
    };

    var reporter = new Thread(() =>
    {
        long previous = 0;
        long previousBytes = 0;
        while (!stop)
        {
            Thread.Sleep(1000);
            long current = Interlocked.Read(ref packets);
            long currentBytes = Interlocked.Read(ref bytes);
            long pps = current - previous;
            double mbit = (currentBytes - previousBytes) * 8 / 1_000_000.0;
            Console.WriteLine($"rx {pps,11:N0} pps {mbit,8:N1} Mbit/s | total {current:N0}");
            previous = current;
            previousBytes = currentBytes;
        }
    })
    { IsBackground = true };
    reporter.Start();

    Console.WriteLine($"sink listening on :{port}, Ctrl+C for summary");

    byte[] buffer = GC.AllocateArray<byte>(65536, pinned: true);
    try
    {
        while (!stop)
        {
            int received = socket.Receive(buffer);
            Interlocked.Increment(ref packets);
            Interlocked.Add(ref bytes, received);
            if (received >= 8)
            {
                long sequence = BinaryPrimitives.ReadInt64LittleEndian(buffer);
                if (sequence < minSequence) minSequence = sequence;
                if (sequence > maxSequence) maxSequence = sequence;
            }
        }
    }
    catch (SocketException) when (stop)
    {
        // socket closed by the Ctrl+C handler
    }
    catch (ObjectDisposedException)
    {
        // socket closed by the Ctrl+C handler
    }

    Console.WriteLine($"received: {packets:N0} packets, {bytes:N0} bytes");
    if (maxSequence >= 0)
    {
        long expected = maxSequence - minSequence + 1;
        long lost = expected - packets;
        Console.WriteLine(
            $"sequence span: {minSequence:N0}..{maxSequence:N0} " +
            $"({expected:N0} expected, {lost:N0} lost, {100.0 * lost / expected:N3} %)");
    }
    return 0;
}

static IPEndPoint ResolveEndPoint(string value)
{
    if (IPEndPoint.TryParse(value, out IPEndPoint? parsed))
    {
        return parsed;
    }

    int colon = value.LastIndexOf(':');
    if (colon <= 0 || colon == value.Length - 1)
    {
        throw new ArgumentException($"'{value}' is not a host:port pair");
    }

    string host = value[..colon];
    int port = int.Parse(value[(colon + 1)..]);
    IPAddress address = Dns.GetHostAddresses(host)
        .First(a => a.AddressFamily == AddressFamily.InterNetwork);
    return new IPEndPoint(address, port);
}
