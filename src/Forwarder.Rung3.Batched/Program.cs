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

ForwarderOptions options;
try
{
    options = ForwarderOptions.Parse(args);
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
    $"rung 3 (RIO): listening on :{options.ListenPort}, " +
    $"forwarding to {options.Destinations.Count} destination(s)");

var forwarder = new RioForwarder(options, stats);
forwarder.Run(cts.Token);
return 0;
