using System.Net;
using System.Net.Sockets;

namespace Forwarder.Core;

public sealed class ForwarderOptions
{
    public required int ListenPort { get; init; }
    public required IReadOnlyList<IPEndPoint> Destinations { get; init; }
    public TimeSpan StatsInterval { get; init; } = TimeSpan.FromSeconds(1);

    public const string Usage =
        "usage: --listen <port> --to <host:port> [--to <host:port> ...] [--stats <seconds>]";

    public static ForwarderOptions Parse(string[] args)
    {
        int listenPort = 5000;
        var destinations = new List<IPEndPoint>();
        var statsInterval = TimeSpan.FromSeconds(1);

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--listen":
                    listenPort = int.Parse(args[++i]);
                    break;
                case "--to":
                    destinations.Add(ResolveEndPoint(args[++i]));
                    break;
                case "--stats":
                    statsInterval = TimeSpan.FromSeconds(double.Parse(args[++i]));
                    break;
                default:
                    throw new ArgumentException($"unknown argument '{args[i]}'; {Usage}");
            }
        }

        if (destinations.Count == 0)
        {
            throw new ArgumentException($"at least one --to destination is required; {Usage}");
        }

        return new ForwarderOptions
        {
            ListenPort = listenPort,
            Destinations = destinations,
            StatsInterval = statsInterval,
        };
    }

    public static IPEndPoint ResolveEndPoint(string value)
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
