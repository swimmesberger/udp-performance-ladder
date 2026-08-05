// Rung 2: the frugal loop. Same syscall pattern as rung 1 (one receive,
// N sends per datagram) but with the allocations removed: a raw Socket,
// one pinned receive buffer reused forever, destinations resolved to
// SocketAddress once at startup, and the .NET 8 ReceiveFromAsync /
// SendToAsync overloads that complete without allocating.
using System.Net;
using System.Net.Sockets;
using Forwarder.Core;

// --sync runs the same loop with blocking calls instead of async/await:
// the control for the rung 4 (Rust) comparison, whose loop also blocks.
bool synchronous = args.Contains("--sync");
args = args.Where(a => a != "--sync").ToArray();

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
socket.SendBufferSize = 1 << 20;
socket.DisableUdpConnReset();
socket.Bind(new IPEndPoint(IPAddress.Any, options.ListenPort));

SocketAddress[] destinations = options.Destinations
    .Select(endpoint => endpoint.Serialize())
    .ToArray();

byte[] buffer = GC.AllocateArray<byte>(65536, pinned: true);
Memory<byte> receiveMemory = buffer;
var sender = new SocketAddress(AddressFamily.InterNetwork);

Console.WriteLine(
    $"rung 2 (frugal{(synchronous ? ", sync" : "")}): listening on :{options.ListenPort}, " +
    $"forwarding to {destinations.Length} destination(s)");

if (synchronous)
{
    // Blocking twin of the loop below; a receive timeout keeps Ctrl+C
    // responsive (closing the socket does not reliably wake a blocked
    // sync receive on every OS).
    socket.ReceiveTimeout = 250;
    Span<byte> span = buffer;
    while (!cts.IsCancellationRequested)
    {
        int received;
        try
        {
            received = socket.ReceiveFrom(span, SocketFlags.None, sender);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
        {
            continue;
        }
        stats.PacketReceived(received);

        ReadOnlySpan<byte> datagram = span[..received];
        for (int i = 0; i < destinations.Length; i++)
        {
            try
            {
                socket.SendTo(datagram, SocketFlags.None, destinations[i]);
                stats.PacketForwarded(received);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
            {
                stats.PacketDropped(); // tx backpressure: drop, not crash
            }
        }
    }
    return 0;
}

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
            try
            {
                await socket.SendToAsync(datagram, SocketFlags.None, destinations[i], cts.Token);
                stats.PacketForwarded(received);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
            {
                stats.PacketDropped(); // tx backpressure: drop, not crash
            }
        }
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C
}

return 0;
