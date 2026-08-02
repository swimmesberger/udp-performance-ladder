# Rung 4: the native rewrite

Planned. The frugal (rung 2) and batched (rung 3) forwarder designs
ported to Rust with the architecture kept identical, so the measured
delta isolates the runtime rather than rewarding a redesign.

Planned layout: a cargo workspace with one crate per design
(std sockets first, then io_uring on Linux and Registered I/O on
Windows).
