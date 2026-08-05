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

The rung 1 vs rung 2 verdict is unchanged (tens of MB/s of garbage vs
zero); what the discrepancy adds is its own lesson: the benchmark
quietly measured a different API shape than the shipped loop, caught by
dividing one counter by another. Superseded the same day by the fix
below: the benchmark now measures the forwarder's overloads directly.

## Same day: benchmark standardized on the forwarder's overloads

`ReceivePathBenchmarks` now passes a `CancellationToken` on every call,
so it measures the exact overloads the forwarders ship. Re-measured
(BenchmarkDotNet v0.15.8, .NET 10.0.10, PayloadSize 32):

| Method | Mean | Allocated |
| --- | --- | --- |
| Rung1_UdpClient | 4.708 us | 272 B |
| Rung2_RawSocket | 4.712 us | 72 B |
| Rung2_SendOnly | 4.662 us | 0 |
| Rung2_ReceiveOnly | 4.776 us | 72 B |
| Rung2_FullySynchronous | 4.609 us | 0 |
| Rung1_UdpClient_Loop | 4.415 us | 200 B |
| Rung2_RawSocket_Loop | 4.338 us | 0 |

Consistent with the AllocProbe result: 272 = 200 + the 72 B
benchmark-method state-machine box, and the looped rung 1 reads 200 B
exactly. Means run ~5-10% above the earlier session's run (loopback
timing noise; allocation numbers are exact). Published figures are now
272/200 B for rung 1, 72/0 B for rung 2, with ~200 B/datagram as rung
1's summary number.
