using System.Net;
using System.Net.Sockets;

namespace UdpBench;

public static class EndPoints
{
    public static IPEndPoint Resolve(string value)
    {
        if (IPEndPoint.TryParse(value, out IPEndPoint? parsed))
        {
            return parsed;
        }

        int colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
        {
            throw new ArgumentException($"'{value}' is not a host:port pair");
        }

        string host = value[..colon];
        int port = int.Parse(value[(colon + 1)..]);
        IPAddress address = Dns.GetHostAddresses(host)
            .First(a => a.AddressFamily == AddressFamily.InterNetwork);
        return new IPEndPoint(address, port);
    }
}
