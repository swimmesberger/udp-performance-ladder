# Rung 4: the native rewrite

The forwarder designs ported to Rust with the architecture kept
identical, so the measured delta isolates the runtime rather than
rewarding a redesign.

- `forwarder-rung4`: the frugal loop (rung 2) on std sockets. Done;
  measured ~14-23% less CPU at fixed load than the C# twin and ~10%
  more intake ceiling. See `../results/`.
- io_uring (Linux) and Registered I/O (Windows) variants: planned.

Build with `cargo build --release`; same CLI as the C# rungs
(`--listen`, `--to`, `--stats`).
