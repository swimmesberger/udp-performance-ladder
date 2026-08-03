// udpbench: UDP load generator, measuring sink, and remote-controllable
// benchmark service in one binary.
//
// Every generated datagram carries a 64-bit little-endian sequence number
// in its first 8 bytes, so a sink can derive loss from the gap between the
// sequence span it observed and the packet count it received, without any
// control channel to the sender. The loss calculation assumes a single
// sender per sink.
using System.Net;
using UdpBench;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

return args[0] switch
{
    "send" => RunSend(args[1..]),
    "sink" => RunSink(args[1..]),
    "serve" => ServeCommand.Run(args[1..]),
    "health" => ServeCommand.CheckHealth(args[1..]),
    _ => PrintUsage(),
};

static int PrintUsage()
{
    Console.Error.WriteLine(
        """
        usage:
          udpbench send --target <host:port> [--size <bytes>] [--rate <pps>] [--duration <seconds>]
          udpbench sink --listen <port> [--duration <seconds>]
          udpbench serve [--port <port>]
          udpbench health [--port <port>]

        send options:
          --size      UDP payload bytes, minimum 8 (default 32)
          --rate      packets per second; 0 = unthrottled (default 0)
          --duration  seconds to run; 0 = until Ctrl+C (default 10)

        sink options:
          --duration  seconds to run before printing the summary;
                      0 = until Ctrl+C (default 0)

        serve options:
          --port      HTTP port for the control API (default 5080);
                      set UDPBENCH_API_TOKEN to require a bearer token
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

    IPEndPoint endpoint = EndPoints.Resolve(target);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    Console.WriteLine(
        $"sending to {endpoint}, payload {size} B, " +
        $"rate {(rate == 0 ? "unthrottled" : $"{rate:N0} pps")}, " +
        $"duration {(durationSeconds == 0 ? "until Ctrl+C" : $"{durationSeconds} s")}");

    SendResult result = UdpSender.Run(
        new SendOptions(endpoint, size, rate, durationSeconds), Console.WriteLine, cts.Token);

    Console.WriteLine(
        $"done: {result.PacketsSent:N0} packets in {result.Seconds:N1} s " +
        $"({result.Pps:N0} pps, {result.PayloadMbit:N1} Mbit/s payload)");
    return 0;
}

static int RunSink(string[] args)
{
    int port = 6000;
    int durationSeconds = 0;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--listen":
                port = int.Parse(args[++i]);
                break;
            case "--duration":
                durationSeconds = int.Parse(args[++i]);
                break;
            default:
                Console.Error.WriteLine($"unknown argument '{args[i]}'");
                return PrintUsage();
        }
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    Console.WriteLine(
        $"sink listening on :{port}, " +
        $"{(durationSeconds == 0 ? "Ctrl+C for summary" : $"running {durationSeconds} s")}");

    SinkResult result = UdpSink.Run(
        new SinkOptions(port, durationSeconds), Console.WriteLine, cts.Token);

    Console.WriteLine($"received: {result.Packets:N0} packets, {result.Bytes:N0} bytes");
    if (result.MaxSequence >= 0)
    {
        Console.WriteLine(
            $"sequence span: {result.MinSequence:N0}..{result.MaxSequence:N0} " +
            $"({result.Expected:N0} expected, {result.Lost:N0} lost, {result.LossPercent:N3} %)");
    }
    return 0;
}
