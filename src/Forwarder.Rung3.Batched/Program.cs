// Rung 3: the batched kernel. Windows Registered I/O via hand-written
// interop; the Linux counterpart (io_uring) lives on the roadmap. Same
// serial one-thread pipeline and zero steady-state allocation as rung 2;
// the only changed variable is that I/O requests and completions travel
// through rings shared with the kernel instead of one syscall each.
using Forwarder.Core;
using Forwarder.Rung3;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("rung 3 (RIO) is Windows-only; the Linux counterpart is io_uring, not yet implemented");
    return 2;
}

// Engine flags are this rung's own and are stripped before the shared
// option parser sees the rest.
//   --engine rio        per-request kernel kicks (the published rung 3 engine)
//   --engine rio-defer  RIO_MSG_DEFER + one COMMIT_ONLY per dequeue batch
//   --engine uso        UDP_SEND_MSG_SIZE packed sends (Windows GSO analog)
//   --uro-segment <n>   with uso: opt into URO coalescing, datagrams are n bytes
string engine = "rio";
int uroSegment = 0;
var forwarded = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--engine":
            engine = args[++i];
            break;
        case "--uro-segment":
            uroSegment = int.Parse(args[++i]);
            break;
        default:
            forwarded.Add(args[i]);
            break;
    }
}

ForwarderOptions options;
try
{
    options = ForwarderOptions.Parse(forwarded.ToArray());
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
    $"rung 3 ({engine}{(uroSegment > 0 ? $", uro {uroSegment}" : "")}): " +
    $"listening on :{options.ListenPort}, " +
    $"forwarding to {options.Destinations.Count} destination(s)");

switch (engine)
{
    case "rio":
        new RioForwarder(options, stats, defer: false).Run(cts.Token);
        break;
    case "rio-defer":
        new RioForwarder(options, stats, defer: true).Run(cts.Token);
        break;
    case "uso":
        new UsoForwarder(options, stats, uroSegment).Run(cts.Token);
        break;
    default:
        Console.Error.WriteLine($"unknown engine '{engine}' (rio, rio-defer, uso)");
        return 1;
}
return 0;
