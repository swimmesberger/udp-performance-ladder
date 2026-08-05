using System.Net;
using System.Net.Sockets;
using BenchmarkDotNet.Attributes;

namespace MicroBenchmarks;

/// <summary>
/// Compares the per-datagram cost of the rung 1 (UdpClient) and rung 2
/// (raw Socket + SocketAddress) paths by sending a datagram to the
/// socket's own loopback endpoint and receiving it back. Loopback
/// timing says nothing about wire performance; the number that matters
/// here is Allocated per operation.
///
/// Every call passes a CancellationToken so the benchmark hits the exact
/// overloads the forwarders call. UdpClient's parameterless overloads are
/// a different, Task-returning family that allocates ~160 B more per
/// datagram (see results/2026-08-05-rung1-alloc-overloads.md).
/// </summary>
[MemoryDiagnoser]
public class ReceivePathBenchmarks
{
    private CancellationTokenSource _cts = null!;
    private UdpClient _udpClient = null!;
    private IPEndPoint _udpClientSelf = null!;

    private Socket _rawSocket = null!;
    private SocketAddress _rawSocketSelf = null!;
    private SocketAddress _sender = null!;
    private byte[] _receiveBuffer = null!;

    private byte[] _payload = null!;

    [Params(32)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _cts = new CancellationTokenSource();
        _payload = new byte[PayloadSize];
        _receiveBuffer = GC.AllocateArray<byte>(65536, pinned: true);

        _udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _udpClientSelf = (IPEndPoint)_udpClient.Client.LocalEndPoint!;

        _rawSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _rawSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _rawSocketSelf = ((IPEndPoint)_rawSocket.LocalEndPoint!).Serialize();
        _sender = new SocketAddress(AddressFamily.InterNetwork);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _udpClient.Dispose();
        _rawSocket.Dispose();
        _cts.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> Rung1_UdpClient()
    {
        await _udpClient.SendAsync(_payload, _udpClientSelf, _cts.Token);
        UdpReceiveResult result = await _udpClient.ReceiveAsync(_cts.Token);
        return result.Buffer.Length;
    }

    [Benchmark]
    public async Task<int> Rung2_RawSocket()
    {
        await _rawSocket.SendToAsync(_payload, SocketFlags.None, _rawSocketSelf, _cts.Token);
        return await _rawSocket.ReceiveFromAsync(_receiveBuffer, SocketFlags.None, _sender, _cts.Token);
    }

    /// <summary>Send half of rung 2, to locate the residual allocation.</summary>
    [Benchmark]
    public async Task Rung2_SendOnly()
    {
        await _rawSocket.SendToAsync(_payload, SocketFlags.None, _rawSocketSelf, _cts.Token);
        // drain synchronously so the socket buffer does not fill
        _rawSocket.ReceiveFrom(_receiveBuffer, SocketFlags.None, _sender);
    }

    /// <summary>Receive half of rung 2, to locate the residual allocation.</summary>
    [Benchmark]
    public async Task<int> Rung2_ReceiveOnly()
    {
        _rawSocket.SendTo(_payload, SocketFlags.None, _rawSocketSelf);
        return await _rawSocket.ReceiveFromAsync(_receiveBuffer, SocketFlags.None, _sender, _cts.Token);
    }

    /// <summary>
    /// The same work with blocking calls: no async state machine at all.
    /// The forwarder is a single serial loop pinned to one core anyway, so
    /// this is a legitimate design, not just a benchmark curiosity.
    /// </summary>
    [Benchmark]
    public int Rung2_FullySynchronous()
    {
        _rawSocket.SendTo(_payload, SocketFlags.None, _rawSocketSelf);
        return _rawSocket.ReceiveFrom(_receiveBuffer, SocketFlags.None, _sender);
    }

    private const int Batch = 1000;

    // The benchmarks above call one async method per operation, so each
    // operation pays for its own state machine box. A forwarder runs its loop
    // inside a single long-lived async method. OperationsPerInvoke reproduces
    // that: one invocation, Batch datagrams, results divided by Batch.

    [Benchmark(OperationsPerInvoke = Batch)]
    public async Task<int> Rung1_UdpClient_Loop()
    {
        int last = 0;
        for (int i = 0; i < Batch; i++)
        {
            await _udpClient.SendAsync(_payload, _udpClientSelf, _cts.Token);
            UdpReceiveResult result = await _udpClient.ReceiveAsync(_cts.Token);
            last = result.Buffer.Length;
        }
        return last;
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    public async Task<int> Rung2_RawSocket_Loop()
    {
        int last = 0;
        for (int i = 0; i < Batch; i++)
        {
            await _rawSocket.SendToAsync(_payload, SocketFlags.None, _rawSocketSelf, _cts.Token);
            last = await _rawSocket.ReceiveFromAsync(_receiveBuffer, SocketFlags.None, _sender, _cts.Token);
        }
        return last;
    }
}
