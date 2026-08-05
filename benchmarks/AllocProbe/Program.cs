using System.Net;
using System.Net.Sockets;

// Settles the rung 1 allocation discrepancy: the micro-benchmark reads
// 360 B/datagram while the wire stats line divides to ~245 B/datagram.
// This probe replays the rung 1 loop (UdpClient receive + send, 32 B
// payload) as a self-echo over loopback and measures allocation per
// datagram with GC.GetTotalAllocatedBytes, switching only the overloads:
//
//   plain  - parameterless ReceiveAsync()/SendAsync(byte[], IPEndPoint),
//            what MicroBenchmarks/ReceivePathBenchmarks calls
//            (Task-returning): 360 B/datagram.
//   ct     - CancellationToken overloads, what Forwarder.Rung1.Naive
//            actually calls (ValueTask-returning): 200 B/datagram.
//   realct - same, but with a real CancellationTokenSource token instead
//            of CancellationToken.None: 200 B/datagram (registrations
//            are pooled; no difference).
//
// In-flight depth (1 vs 64) makes no difference over loopback; every
// receive completes synchronously either way. See
// results/2026-08-05-rung1-alloc-overloads.md.
//
// Usage: dotnet run -c Release --project benchmarks/AllocProbe -- <port> <inFlight> [plain|ct|realct]

int port = int.Parse(args[0]);
int inFlight = int.Parse(args[1]);
string mode = args.Length > 2 ? args[2] : "plain";
bool useCt = mode != "plain";
using var cts = new CancellationTokenSource();
CancellationToken ct = mode == "realct" ? cts.Token : CancellationToken.None;
const int Warmup = 50_000;
const int N = 200_000;

var dest = new IPEndPoint(IPAddress.Loopback, port);
using var client = new UdpClient(port);
client.Client.ReceiveBufferSize = 1 << 20;
byte[] payload = new byte[32];

for (int i = 0; i < inFlight; i++)
{
    await client.SendAsync(payload, dest);
}

for (int i = 0; i < Warmup; i++)
{
    await OneIteration();
}

long a0 = GC.GetTotalAllocatedBytes(precise: true);
for (int i = 0; i < N; i++)
{
    await OneIteration();
}
long a1 = GC.GetTotalAllocatedBytes(precise: true);

Console.WriteLine(
    $"inFlight={inFlight,3} mode={mode,-6}: {(a1 - a0) / (double)N,6:F1} B/datagram");

async Task OneIteration()
{
    if (useCt)
    {
        UdpReceiveResult r = await client.ReceiveAsync(ct);
        await client.SendAsync(r.Buffer, dest, ct);
    }
    else
    {
        UdpReceiveResult r = await client.ReceiveAsync();
        await client.SendAsync(r.Buffer, dest);
    }
}
