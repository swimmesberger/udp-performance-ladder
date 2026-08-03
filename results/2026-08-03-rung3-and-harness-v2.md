# Rung 3 (Windows RIO) and the second harness correction, 2026-08-03

Same rig as [2026-08-03-rung1-rung2.md](2026-08-03-rung1-rung2.md). Two harness
changes since, both forced by measurements that could not be true:

1. **Multi-threaded sink.** The single-threaded sink capped near 150k pps over
   the wire (its loopback validation said 400k, the loopback lie in action),
   so every "delivered" figure above that measured the sink. Proof: at 250k
   offered, `fwd_tx == fwd_rx` exactly for rung 1 and rung 3 alike; the loss
   was entirely after the forwarder. The sink now receives on 4 threads
   sharing one socket. On the NAS it still ceilings near 220k pps over the
   wire (more threads make it worse; the box is CPU-bound), so:
2. **Primary cliff metric is now sender count vs the forwarder's own receive
   counter**, baselined after warmup. The sink validates full fan-out below
   its own ceiling; the NIC hardware counters arbitrate disagreements.

## Corrected rung 1 and rung 2 ladders (4 sender threads, 10 s runs, warmed)

Rung 1, naive UdpClient:

| Offered | Rx loss | CPU (one core) |
| ------- | ------- | -------------- |
| 150,000 | 0.77%   | 72.8% |
| 200,000 | 0.11%   | 91.9% |
| 250,000 | 6.59%   | 96.4% |
| 300,000 | 19.30%  | 97.7% |

Rung 2, frugal Socket:

| Offered | Rx loss | CPU (one core) |
| ------- | ------- | -------------- |
| 150,000 | 0.00%   | 74.8% |
| 200,000 | 0.00%   | 88.6% |
| 250,000 | 1.80%   | 96.6% |
| 300,000 | 16.95%  | 97.7% |

The earlier published "cliff at ~175k" was the sink's cliff. Both rungs are
clean at 200k and break between 250k and 300k as one core saturates. The
rung 1 vs rung 2 null result stands: identical within noise.

## Rung 3: Registered I/O

First version tied receive-slot recycling to send completion and starved the
posted-receive ring under load. The signature was loss invisible to every
counter: at 300k offered, generator sent 2,999,658, the NIC hardware counter
received 2,999,923, adapter discards 0, UDP receive errors 0, and the
forwarder saw only 2,624,376. RIO drops on an empty receive ring are recorded
nowhere. The fix is the standard design: separate receive and send buffer
pools, receives repost immediately after a copy into a send slot, and send
pool exhaustion drops in our code where a counter sees it.

Ladder with decoupled pools (8 sender threads, 10 s runs, warmed):

| Offered | Rx loss | Forwarded (tx) | CPU (one core) |
| ------- | ------- | -------------- | -------------- |
| 200,000 | 0.00%   | 200,000/s      | 65.5% |
| 250,000 | 1.76%   | 245,600/s      | 82.2% |
| 300,000 | 1.20%   | 285,700/s      | 98.3% |
| 350,000 | 10.12%  | 287,400/s      | 98.6% |
| 400,000 | 9.50%   | 276,400/s      | 94.8% |

(200k row from the pre-fix run; identical design behavior below saturation.)

## Reading

- **CPU at fixed load is the headline**: at 200,000 pps, rung 3 does the same
  work as rungs 1 and 2 for 65.5% of a core against ~90%. The removed cost is
  the per-packet syscall pair; the ~4 us/datagram budget drops by roughly a
  quarter.
- **Throughput ceiling moves from ~245k to ~286k pps** (+17%), now limited by
  the send path (one RIOSendEx per packet plus one copy). Receives alone keep
  up to ~360k.
- Above ~300k the generator's own burstiness (sleep-based pacing wakes with a
  deficit and sends bursts) contributes to rx loss; treat 350k+ rows as
  approximate.

## Caveats

- Same non-idle-machine caveats as the previous session; loss figures within
  ~1-2% of zero are within run-to-run variance.
- Sink-delivered numbers above ~220k pps are harness-limited and not
  reported as forwarder loss anywhere here.

## Revision: zero-copy slot rotation

The decoupled-pool fix above paid one memcpy per packet. Unnecessary: a single
pre-allocated pool whose slots rotate roles keeps zero-copy AND the guarantee.
Each receive completion posts exactly one replacement receive from the free
pool and sends directly from the filled slot (returned to the pool when its
sends complete); if the pool is empty, the filled slot itself is reposted and
that one datagram is dropped on the forwarder's own counter. Posted receives
are constant by construction.

| Offered | Rx loss | Forwarded (tx) | CPU (one core) |
| ------- | ------- | -------------- | -------------- |
| 200,000 | 0.80%   | 198,400/s      | 66.1% |
| 250,000 | 0.00%   | 250,000/s      | 87.3% |
| 300,000 | 0.90%   | 275,900/s      | 95.5% |
| 350,000 | 2.56%   | 272,000/s      | 94.2% |

Receive side improves markedly at overload (350k: 10.12% -> 2.56% rx loss);
the tx ceiling stays ~276k/s (one RIOSendEx post per packet). Note: still
zero heap allocation in steady state in all rung 3 variants; the memcpy in
the interim design was a bandwidth cost, not an allocation.

## Batched harness (sendmmsg/recvmmsg) and the flow-control discovery

With batching on both sides, the harness ceilings moved decisively:

| Component | Old | Batched |
| --- | --- | --- |
| Generator over the wire, unthrottled | ~540k pps | ~618k pps |
| Generator+sink NAS-local, concurrent | ~495k / ~400k | ~725k pps at 3.6% loss |

Rung 3 re-measured with clean (non-bursty) offered load:

| Offered | Rx loss | CPU |
| ------- | ------- | --- |
| 300,000 | 0.18%   | 80.3% |
| 350,000 | 0.04%   | 79.1% |
| 400,000 | 0.66%   | 81.7% |
| 450,000 | 6.45%   | 80.9% |

Receive capacity is ~420k pps, notably better than measured under the old
bursty generator. But the forwarding (tx) rate FELL to ~210k/s at only ~80%
CPU, versus ~276k earlier. Cause identified but not yet removed: Ethernet
flow control is enabled on the DUT NIC (Rx & Tx), so the NAS, now much
busier, can PAUSE the workstation's transmit at the MAC layer. The
forwarding ceiling currently measures the harness receiver's ingestion
rate through PAUSE frames, not the forwarder.

Open item before tx numbers are publishable: disable flow control on the
DUT NIC (and Energy-Efficient Ethernet / Green Ethernet / Power Saving
Mode, all currently enabled), or move the fan-out destination to a box
that is not also the generator.

## Arrival pattern is a benchmark variable: burst tolerance

The batched generator emits per-wakeup bursts (up to 64 datagrams at line
rate per thread) instead of the old smooth stream. Same offered averages,
very different results for the small-buffer serial rungs:

| Rung, 150k offered | Smooth generator | Bursty generator |
| --- | --- | --- |
| 1, naive (default ~64 KB rcvbuf) | 0.77% loss | 9.68% loss |
| 2, frugal (1 MB rcvbuf)          | 0.00% loss | 2.46% loss |
| 3, RIO (4096 posted receives)    | 0.82% loss | 0.00% loss |

Under bursty arrivals rungs 1/2 wobble from 150k and break by 250k, while
rung 3 stays clean to 400k+: deep posted-receive rings absorb bursts that
overflow a socket buffer being drained 4 us at a time. CPU at 200k offered
(bursty): rung 1 83.9%, rung 2 82.5%, rung 3 63.3%.

Consequence: the final published matrix must fix one arrival profile
(bursty, being both harder and closer to aggregated real traffic) and note
it in the methodology. Pending the NIC hygiene pass (flow control, EEE,
Green Ethernet, power saving all currently enabled on the DUT), after
which the full three-rung matrix gets one definitive run.

## FINAL matrix (definitive configuration)

DUT NIC hygiene applied: flow control OFF, EEE OFF, Green Ethernet OFF,
Power Saving Mode OFF, Gigabit Lite OFF, receive buffers 4096 (driver max),
interrupt moderation left ON (throughput-realistic default). Batched
generator, 8 threads, line-rate bursts up to 64; 10 s runs, 3 s discarded
warmup; loss = sender count vs forwarder rx counter.

Flow-control note: with PAUSE enabled the DUT *looked* better (0.66% rx
loss at 400k) because the NAS was being silently throttled at the MAC
layer. PAUSE off exposes true per-layer capacity; it also un-throttled the
forwarder's tx, which now equals rx at every rate (rung 3 drop counter 0).

Rung 1, naive UdpClient (default ~64 KB socket buffer):

| Offered | Rx loss | CPU |
| ------- | ------- | --- |
| 150,000 | 0.55%   | 73.3% |
| 200,000 | 2.22%   | 84.1% |
| 250,000 | 9.47%   | 90.5% |
| 300,000 | 14.69%  | 98.6% |

Rung 2, frugal Socket (1 MB socket buffer):

| Offered | Rx loss | CPU |
| ------- | ------- | --- |
| 150,000 | 0.00%   | 71.9% |
| 200,000 | 0.00%   | 88.0% |
| 250,000 | 0.12%   | 95.6% |
| 300,000 | 11.76%  | 101.2% |

Rung 3, Registered I/O (4096 posted receives):

| Offered | Rx loss | CPU |
| ------- | ------- | --- |
| 150,000 | 0.00%   | 50.8% |
| 200,000 | 0.28%   | 63.1% |
| 250,000 | 1.81%   | 80.5% |
| 300,000 | 6.41%   | 88.6% |
| 400,000 | 20.39%  | 97.0% (peak sustained rx ~306k/s) |

Reading: under bursty arrivals rung 2 now beats rung 1 (clean to 250k vs
wobbling from 150k). Attribution caveat: rung 2 also sets a 1 MB receive
buffer where UdpClient defaults to ~64 KB, and the burst-tolerance edge is
likely mostly that buffer, not the allocation work. Rung 3 forwards
everything it receives at every rate, saturates ~306k pps rx / one core,
and does 200k for 63% of a core vs 84-88% for rungs 1/2.

## Control run: rung 1 with the receive buffer aligned to 1 MB

UdpClient leaves SO_RCVBUF at the OS default (~64 KB); rung 2 sets 1 MB.
That confound owned the entire burst-tolerance gap. Rung 1 with
`client.Client.ReceiveBufferSize = 1 << 20` (now the committed code; all
rungs aligned at 1 MB):

| Offered | Rx loss (aligned) | Rx loss (64 KB default) | CPU |
| ------- | ----------------- | ----------------------- | --- |
| 150,000 | 0.00%             | 0.55%                   | 74.4% |
| 200,000 | 0.00%             | 2.22%                   | 88.0% |
| 250,000 | 0.78%             | 9.47%                   | 97.8% |
| 300,000 | 14.92%            | 14.69%                  | 101.7% |

Aligned, rungs 1 and 2 are statistically identical in every column: the
pure null result stands, and the burst-tolerance win belonged to the
buffer, not the allocation work.

## Rung 4: Rust, std sockets (same architecture as rung 2)

Identical design: one thread, blocking recv_from, one send_to per
destination, one reused buffer, destinations resolved once, aligned 1 MB
receive buffer (socket2). Same harness, same profile, same protocol.

| Offered | Rx loss | CPU (one core) | C# rung 2 CPU |
| ------- | ------- | -------------- | ------------- |
| 150,000 | 0.00%   | 55.2% | 71.9% |
| 200,000 | 0.00%   | 75.9% | 88.0% |
| 250,000 | 0.00%   | 92.2% | 95.6% |
| 300,000 | 6.42%   | 100.9% | 101.2% (11.76% loss) |
| 350,000 | 18.61%  | 101.1% (rx ~285k/s) | - |

Reading: the runtime swap buys roughly 14-23% CPU at fixed load
(3.8 us/datagram vs 4.4 at 200k) and ~10% intake ceiling (~285k vs
~260k pps). Notably, C# on Registered I/O (rung 3: 50.8% / 63.1% CPU at
150k/200k) still beats Rust on plain syscalls at every rate: the kernel
interface is worth more than the language.

Note: first wire attempt read 100% loss with 0% CPU; the Windows firewall
had no rule for the new unsigned binary. Allow rule added, re-run.

## Control: C# blocking loop (rung 2 --sync) vs Rust

Rung 2's loop is async; rung 4's Rust loop blocks. Different dispatch
model, not just a different language, so the Rust comparison owed a
control: the same C# loop on .NET 8's synchronous SocketAddress overloads
(Forwarder.Rung2.Frugal --sync).

| Offered | Rust (blocking) | C# blocking | C# async |
| ------- | --------------- | ----------- | -------- |
| 150,000 | 55.2% | 59.1% | 71.9% |
| 200,000 | 75.9% | 80.0% | 88.0% |
| 250,000 | 92.2% | 98.3% | 95.6% |
| 300,000 | 6.42% loss | 11.89% loss | 11.76% loss |

Roughly two thirds of the apparent Rust win was the async dispatch
machinery; the true language/runtime delta is ~5% (3.8 vs 4.0 us/datagram
at 200k). The async penalty shrinks as load rises (saturated receives
complete synchronously), converging by 250k. Both C# builds are Release;
dynamic PGO is on by default in .NET 10 and the discarded warmup covers
tiered JIT promotion; Rust built with lto=true, codegen-units=1.

Updated CPU ranking at 200k pps: C# async 88% > C# blocking 80% >
Rust blocking 76% > C# RIO 63%. Kernel interface > dispatch model >
language.
