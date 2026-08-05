# UDP performance ladder: consolidated results

Rollup of every measured rung as of 2026-08-03. Detail and methodology in
the dated files in this directory; environment and repro in
`../bench/ENVIRONMENT.md`.

## Windows, bare metal, 1 GbE LAN (the clean set)

32-byte payloads, bursty batched generator, receive buffer aligned at 1 MB
on every rung, CPU = one core at a sustained 200,000 pps.

| Rung / variant | Clean to | Breaks by | CPU @ 200k | Alloc |
| --- | --- | --- | --- | --- |
| 1 naive UdpClient (async) | 250k | 300k | 88% | ~200 B/dgram |
| 2 frugal Socket (async) | 250k | 300k | 88% | 0 |
| 2 frugal Socket (--sync) | 250k | 300k | 80% | 0 |
| 4 Rust std sockets (blocking) | 250k | ~285k | 76% | 0 (no GC) |
| 4 Rust tokio (async) | 250k | ~285k | 79% | 0 |
| 3 C# Registered I/O | 250k | ~306k | 63% | 0 |
| 4 Rust on RIO | 300k | ~336k | **58%** | 0 |

Winner: **Rust on RIO** (58%), then C# RIO (63%). The interface dominates;
the language is a ~5% additive delta on top (Rust+RIO = C#RIO - 5%,
predicted 60%, measured 58%).

## Rung 3 Windows race (2026-08-05; same-day A/B, busier machine, median
## stat — columns compare to each other, NOT to the table above)

Defender + firewall OFF, driver 10.80.20.407 (the published set):

| Engine | CPU @ 200k | Notes |
| --- | --- | --- |
| rio (per-request kicks) | 56% | same engine as the published 63% row |
| rio-defer (DEFER + COMMIT_ONLY per batch) | 50% | clean to 300k, ~400k intake at 0.5% loss |
| uso (UDP_SEND_MSG_SIZE packed sends) | **28%** | plain sockets; 35.5% at 300k (2.4x cheaper than rio) |
| uso + uro | 61% | URO never coalesces anywhere (diagnosed) -> per-packet sends |

Windows mirrors Linux family-for-family: stack batching (USO/GSO) >
transition batching (defer/mmsg) > per-request ring calls. Bonus A/B:
Defender real-time protection costs per-packet engines ~10-12 points of
a core at 200k+ and barely touches USO (the filtering stack is a
per-packet cost too). See 2026-08-05-rung3-windows-race.md.

## Linux over the real LAN (virtualized WSL adapter — comparable only to each other)

CPU self-reported, sustained 200k pps, zero loss:

| Engine | CPU @ 200k | Notes |
| --- | --- | --- |
| C# frugal Socket async (epoll) | ~131% | 1.3 cores; .NET async sockets are bad here |
| C# frugal Socket sync | ~43% | |
| mmsg (recvmmsg/sendmmsg) | ~36% | |
| mmsg + UDP GSO | ~26% | most robust across the range |
| AF_PACKET mmap rings | ~13% | cheapest, but softirq work is unbilled |

Winner within Linux sockets: **mmsg + UDP GSO** on throughput robustness
(1% loss at 300k vs mmsg's 5.7%); AF_PACKET cheapest CPU but falls off a
cliff past 200k (single-threaded, all header work in-process).

## The hierarchy (the post's thesis, measured)

Ranked by CPU effect, largest first:

1. **The kernel interface** (kernel + its native I/O API — inseparable, RIO
   is Windows-only, io_uring/GSO/XDP Linux-only): ~25-30% on Windows for
   rings, ~2x for USO vs per-request RIO (2026-08-05 race); on Linux
   GSO/AF_PACKET are 1.4-2.7x cheaper than plain mmsg. Within the
   interface axis the family order is the same on both OSes:
   stack batching (GSO/USO) > transition batching (mmsg/defer) > rings.
2. **Dispatch model** (async vs blocking): ~10-15% on Windows; up to 3x on
   Linux (.NET async epoll = 131% vs sync 43%).
3. **Language** (Rust vs C#): ~5%, additive.

Allocation removal (Span/pooling): **zero** throughput/CPU effect once
measured — it was never the bottleneck. The one config knob that mattered
as much as any rewrite was the socket receive buffer size.

Cross-OS ranking is NOT possible cleanly: the OS effect flips sign with
dispatch model (same C# code: Linux cheaper blocking, dearer async), and is
confounded by virtualization (inflates) and softirq accounting (deflates),
which push opposite ways.

## Recurring lesson (happened 5+ times)

Every dramatic-looking result that turned out wrong came from measuring the
wrong thing, caught by counting at both ends:
- loopback hides NIC/driver cost (all rungs); the single-threaded sink
  capped throughput and masqueraded as forwarder loss;
- RIO's first design dropped packets invisibly (no OS counter);
- AF_PACKET counted queued frames as delivered (sink got nothing);
- multi-thread sink over-reported loss from ragged sequence tails;
- virtualized XDP would measure a fallback path;
- the micro-benchmark measured the Task-returning UdpClient overloads
  while the forwarder runs the ValueTask ct-overloads (360 vs 200 B/dgram;
  see 2026-08-05 note), caught by dividing the wire counter by pps.
  Fixed at the source: the benchmark now passes a CancellationToken so it
  measures the forwarder's exact overloads (272/200 B single/looped).
Assert delivery at the sink; assert you got the mode/interface you asked
for; state the arrival profile; do not trust loopback for wire claims.
