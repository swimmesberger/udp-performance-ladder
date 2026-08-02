// Rung 2: the frugal loop. Same syscall pattern as rung 1 (one receive,
// N sends per datagram) but with the allocations removed: a raw Socket,
// one pinned receive buffer reused forever, destinations resolved to
// SocketAddress once at startup, and the .NET 8 ReceiveFromAsync /
// SendToAsync overloads that complete without allocating.
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

using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
socket.ReceiveBufferSize = 1 << 20;
socket.Bind(new IPEndPoint(IPAddress.Any, options.ListenPort));

SocketAddress[] destinations = options.Destinations
    .Select(endpoint => endpoint.Serialize())
    .ToArray();

byte[] buffer = GC.AllocateArray<byte>(65536, pinned: true);
Memory<byte> receiveMemory = buffer;
var sender = new SocketAddress(AddressFamily.InterNetwork);

Console.WriteLine(
    $"rung 2 (frugal): listening on :{options.ListenPort}, " +
    $"forwarding to {destinations.Length} destination(s)");

try
{
    while (!cts.IsCancellationRequested)
    {
        int received = await socket.ReceiveFromAsync(
            receiveMemory, SocketFlags.None, sender, cts.Token);
        stats.PacketReceived(received);

        ReadOnlyMemory<byte> datagram = receiveMemory[..received];
        for (int i = 0; i < destinations.Length; i++)
        {
            await socket.SendToAsync(datagram, SocketFlags.None, destinations[i], cts.Token);
            stats.PacketForwarded(received);
        }
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C
}

return 0;
