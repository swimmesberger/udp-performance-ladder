# Rung 5: the stack bypass

Planned. The forwarder rebuilt on AF_XDP (Linux) and
[XDP-for-Windows](https://github.com/microsoft/xdp-for-windows),
receiving and transmitting at or near the network driver. At this
layer the forwarder parses and rewrites Ethernet/IP/UDP headers and
recomputes checksums itself; there is no socket API doing it for us.

Requires NICs/drivers with native XDP support to be meaningful; the
generic fallback mode re-enters the kernel network stack and is not
representative.
