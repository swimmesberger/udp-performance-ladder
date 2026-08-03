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
