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
// The one deliberate non-default: UdpClient leaves the socket receive
// buffer at the OS default (~64 KB), which overflows on line-rate bursts
// long before the loop itself is the limit (measured: 9.5% loss at
// 250k pps with the default vs the aligned buffer). Every rung uses 1 MB
// so the ladder isolates one variable per rung; the default is the trap.
client.Client.ReceiveBufferSize = 1 << 20;
client.Client.SendBufferSize = 1 << 20;
// Windows: without this, one inbound ICMP port-unreachable (a briefly
// unbound destination) surfaces as ConnectionReset and kills the loop.
client.Client.DisableUdpConnReset();
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
            try
            {
                await client.SendAsync(datagram.Buffer, destination, cts.Token);
                stats.PacketForwarded(datagram.Buffer.Length);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
            {
                // Transmit backpressure (WSAENOBUFS): for UDP that is a
                // drop, counted on our own counter, not a crash.
                stats.PacketDropped();
            }
        }
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C
}

return 0;
