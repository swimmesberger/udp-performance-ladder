using System.Net.Sockets;

namespace Forwarder.Core;

public static class SocketHardening
{
    // SIO_UDP_CONNRESET (0x9800000C). On Windows, an inbound ICMP
    // port-unreachable (for example a sink that is not bound yet) surfaces
    // as SocketError.ConnectionReset on the NEXT receive of the socket that
    // sent the offending datagram, and an unhandled one kills a naive
    // forwarder loop. Off by default it is masked when the Windows Firewall
    // drops inbound ICMP; with the firewall disabled it arrives. Disabling
    // the behavior at the socket keeps UDP semantics honest: a datagram
    // send has no delivery contract, so a send must not poison receives.
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    public static void DisableUdpConnReset(this Socket socket)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
    }
}
