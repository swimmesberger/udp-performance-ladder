// Rung 1: the naive loop. UdpClient, one await per operation, a fresh
// buffer and sender IPEndPoint allocated per received datagram. This is
// the baseline every other rung is measured against; do not optimize it.
using System.Net;
using System.Net.Sockets;
using Forwarder.Core;

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

using var client = new UdpClient(options.ListenPort);
Console.WriteLine(
    $"rung 1 (naive): listening on :{options.ListenPort}, " +
    $"forwarding to {options.Destinations.Count} destination(s)");

try
{
    while (!cts.IsCancellationRequested)
    {
        UdpReceiveResult datagram = await client.ReceiveAsync(cts.Token);
        stats.PacketReceived(datagram.Buffer.Length);
        foreach (IPEndPoint destination in options.Destinations)
        {
            await client.SendAsync(datagram.Buffer, destination, cts.Token);
            stats.PacketForwarded(datagram.Buffer.Length);
        }
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C
}

return 0;
