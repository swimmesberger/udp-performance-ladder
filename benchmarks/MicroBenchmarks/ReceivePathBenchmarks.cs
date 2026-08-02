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
/// </summary>
[MemoryDiagnoser]
public class ReceivePathBenchmarks
{
    private UdpClient _udpClient = null!;
    private IPEndPoint _udpClientSelf = null!;

    private Socket _rawSocket = null!;
    private SocketAddress _rawSocketSelf = null!;
    private SocketAddress _sender = null!;
    private byte[] _receiveBuffer = null!;

    private byte[] _payload = null!;

    [Params(32, 1200)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
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
    }

    [Benchmark(Baseline = true)]
    public async Task<int> Rung1_UdpClient()
    {
        await _udpClient.SendAsync(_payload, _udpClientSelf);
        UdpReceiveResult result = await _udpClient.ReceiveAsync();
        return result.Buffer.Length;
    }

    [Benchmark]
    public async Task<int> Rung2_RawSocket()
    {
        await _rawSocket.SendToAsync(_payload, SocketFlags.None, _rawSocketSelf);
        return await _rawSocket.ReceiveFromAsync(_receiveBuffer, SocketFlags.None, _sender);
    }
}
