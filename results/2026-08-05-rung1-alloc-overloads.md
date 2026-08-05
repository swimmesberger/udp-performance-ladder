# Rung 1 allocation per datagram: the overload discrepancy, resolved

Date: 2026-08-05. Probe source: `benchmarks/AllocProbe` (UdpClient
self-echo over loopback, 32 B payload, 200k measured iterations after a
50k warmup, `GC.GetTotalAllocatedBytes(precise: true)`).

## The discrepancy

The micro-benchmark (`ReceivePathBenchmarks`, looped) reports rung 1 at
**360 B/datagram**. The running forwarder's wire stats line reads
36.8 MB/s at 150,024 pps, which divides to **~245 B/datagram**. Both
numbers are real; they measure different programs.

## The cause: different overloads

- The benchmark calls the parameterless `UdpClient.ReceiveAsync()` /
  `SendAsync(byte[], IPEndPoint)` overloads, which allocate a fresh
  `Task` per operation. Probe: **360.0 B/datagram**.
- `Forwarder.Rung1.Naive` passes a `CancellationToken`, which selects
  the `ValueTask`-returning overloads. Probe: **200.0 B/datagram**.

Controls, all no-ops:

- Real `CancellationTokenSource` token vs `CancellationToken.None`:
  identical (200.0 B; per-operation registrations are pooled).
- In-flight depth 1 vs 64: identical (over loopback every receive
  completes synchronously either way, so completion mode is not the
  variable).

The remaining ~45 B/datagram between the probe's 200 B and the wire's
~245 B is, by subtraction, the process around the loop (stats reporting,
occasional suspensions between bursts), unattributed in detail.

## Consequence for published numbers

Rung 1's per-datagram figure on the wire is **~245 B** (the loop itself
200 B); 360 B describes the plain overloads the benchmark happens to
call. The blog post and SUMMARY.md now carry ~245 B. The rung 1 vs
rung 2 verdict is unchanged (tens of MB/s of garbage vs zero); what the
discrepancy adds is its own lesson: the benchmark quietly measured a
different API shape than the shipped loop, caught by dividing one
counter by another.
