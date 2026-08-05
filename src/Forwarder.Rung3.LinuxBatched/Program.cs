// Rung 3, Linux half: the batched-kernel race. Two engines behind one CLI:
//   --engine mmsg    recvmmsg/sendmmsg, a batch of datagrams per syscall
//   --engine uring   io_uring rings, raw syscall interop (no liburing)
// The plain-loop baseline for the race is Forwarder.Rung2.Frugal --sync.
using Forwarder.Core;
using Forwarder.Rung3.Linux;

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("this rung is Linux-only (the Windows counterpart is Forwarder.Rung3.Batched)");
    return 2;
}

string engine = "mmsg";
var filtered = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--engine")
    {
        engine = args[++i];
    }
    else
    {
        filtered.Add(args[i]);
    }
}

ForwarderOptions options;
try
{
    options = ForwarderOptions.Parse(filtered.ToArray());
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var stats = new ForwarderStats();
using var reporter = StatsReporter.Start(stats, options.StatsInterval);

Console.WriteLine(
    $"rung 3 linux ({engine}): listening on :{options.ListenPort}, " +
    $"forwarding to {options.Destinations.Count} destination(s)");

switch (engine)
{
    case "mmsg":
        MmsgEngine.Run(options, stats, cts.Token);
        break;
    case "uring":
        IoUringEngine.Run(options, stats, cts.Token);
        break;
    case "gso":
        GsoEngine.Run(options, stats, gro: false, cts.Token);
        break;
    case "gso-gro":
        GsoEngine.Run(options, stats, gro: true, cts.Token);
        break;
    case "uring-gso":
        UringGsoEngine.Run(options, stats, gro: false, cts.Token);
        break;
    case "uring-gso-gro":
        UringGsoEngine.Run(options, stats, gro: true, cts.Token);
        break;
    case "afpacket":
        AfPacketEngine.Run(options, stats, cts.Token);
        break;
    default:
        Console.Error.WriteLine($"unknown engine '{engine}' (mmsg | uring | gso | gso-gro | uring-gso | uring-gso-gro | afpacket)");
        return 1;
}
return 0;
