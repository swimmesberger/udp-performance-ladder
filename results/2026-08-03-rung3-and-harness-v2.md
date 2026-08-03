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

## Rust async (tokio, current_thread): the async tax is not inherent

Same forwarder, same single logical thread, loop calls changed to .await
(tokio::net::UdpSocket on a current_thread runtime):

| Offered | Rust blocking | Rust tokio | C# blocking | C# async |
| ------- | ------------- | ---------- | ----------- | -------- |
| 150,000 | 55.2% | 52.5% | 59.1% | 71.9% |
| 200,000 | 75.9% | 79.2% | 80.0% | 88.0% |
| 250,000 | 92.2% | 94.2% | 98.3% | 95.6% |

Tokio's async costs nothing measurable vs blocking Rust (differences are
within run variance), while .NET async costs 10-15% of a core at these
rates. The tax is the dispatch implementation (IOCP completion -> thread
pool continuation per op in .NET, vs stack-allocated state machine polled
on the reactor thread in tokio), not the async concept. Caveat: single
task on current_thread is tokio's best case.

Updated 200k ranking: C# async 88% > C# blocking 80% > tokio 79% >
Rust blocking 76% > C# RIO 63%.

## Corrections and plan notes (accuracy pass)

- The tokio-vs-.NET mechanism claims were tightened: the Rust state machine
  is stack-pinned *in this program's shape* (block_on; a spawned task is one
  heap allocation), and .NET skips the IOCP round trip on synchronously
  completing operations (FILE_SKIP_COMPLETION_PORT_ON_SUCCESS), which is
  the mechanism behind the async tax fading at saturation. The 10-15%
  measured at moderate load is the cost of actual suspensions.
- Rung 3 Linux plan revised to a three-way race: plain loop vs
  sendmmsg/recvmmsg vs io_uring. Rationale: the generator's own mmsg path
  does >1.3M pps in a container, so batch-per-syscall may capture most of
  the ring win at far lower complexity; io_uring additionally is blocked by
  default container seccomp profiles, which matters for containerized
  deployment and for this project's own Linux test environment.

## The Linux race: plain loops vs mmsg vs io_uring

Environment: container on the local podman machine (WSL2 kernel 6.6.87,
16 vCPUs, .NET 10.0.10), generator/sink/forwarder all over container
loopback. Absolute numbers are ~5x below bare-metal Windows on the same
hardware (virtualized syscall path); only the relative comparison on this
identical footing is meaningful. Sustained per-engine ceilings, engine
saturated, 10 s windows:

| Engine | Ceiling (pps) | CPU while saturated | pps per core |
| ------ | ------------- | ------------------- | ------------ |
| rung 2 async (epoll, .NET native path) | ~26,000  | 237% (multi-core) | ~11,000 |
| rung 2 --sync (emulated over epoll)    | ~50,000  | 100% | ~50,000 |
| mmsg batching                          | ~65,000-80,000 | 100% | ~65,000-80,000 |
| io_uring (this port)                   | ~55,000  | 100% | ~55,000 |

Findings:
1. **mmsg beats io_uring for this workload**, by ~20-45%. The skeptical
   question ("is io_uring even the fastest option here?") is answered:
   not in this straightforward port. Unused io_uring modes (multishot
   recvmsg, SQPOLL, registered buffers) might reverse it; unmeasured.
2. **Default container seccomp blocks io_uring outright** (io_uring_setup
   fails; the engine cannot start without seccomp=unconfined). mmsg runs
   everywhere.
3. **.NET's async socket engine collapses under overload on this Linux
   loopback**: 2.4 cores burned to deliver ~26k pps, worse per core than
   every other lane by 5x. Its sync path (a poll+recv emulation over the
   same engine) does ~50k on one core.
4. All engines forwarded everything they received (fwd_tx == fwd_rx).

Caveats: loopback arrival pattern, WSL2 virtualization, and an io_uring
implementation using one enter per completion batch. Treat as a ranking,
not as capacity numbers.

### Why mmsg's win should survive io_uring tuning (assessment)

Both interfaces run the same kernel per-datagram UDP path and both already
amortize the user-kernel transition, so the contest is per-packet wrapper
cost: a bare loop (mmsg) vs ring bookkeeping + io_kiocb + task work
(io_uring). Available levers for the port: multishot recvmsg + provided
buffer rings (removes per-packet submission; plausibly reaches parity),
SQPOLL (removes the last syscall but burns a dedicated core; a loss on
pps/core), registered buffers / SEND_ZC (large payloads only). io_uring's
structural advantages (mixed I/O, chaining, zero-copy sends) go unused by
tiny-datagram fan-out. Prediction if a rematch is run: parity +/- 10%.

### The Linux ceiling below XDP/eBPF (assessment)

mmsg is nearly but not exactly the socket-API ceiling. Above it:
- UDP_SEGMENT (GSO): one send call carries a buffer of equal-size
  payloads + segment size; the kernel/NIC splits after one stack
  traversal. Fits fan-out well (same bytes, one destination per send);
  QUIC stacks ship on it. Receive twin (UDP GRO) needs equal-size runs.
  Candidate optional rung: mmsg + GSO.
- AF_PACKET + PACKET_MMAP (TPACKET_V3): raw-frame rings, no eBPF, no
  driver requirements, near-zero syscalls, but you already pay raw-frame
  costs (header parse/build/checksum) - most of rung 5's complexity for
  less of its win. Not worth a rung.
- DPDK is not eBPF but is more invasive than XDP (owns the NIC); it
  belongs at/beyond rung 5.

Practical Linux ladder below XDP: plain loop -> mmsg -> mmsg+GSO, stop.

## Six-lane Linux race: UDP GSO wins decisively; AF_PACKET tx unverified

Same container harness (WSL2, loopback, 10 s windows). Sustained forwarding
with loss measured sender-count vs forwarder-rx, CPU from /proc:

| Engine | 400k offered | CPU | Effective pps/core |
| ------ | ------------ | --- | ------------------ |
| plain async (epoll) | 93.5% loss (~26k pps) | 238% | ~11,000 |
| plain sync (emulated) | 87.8% loss (~49k pps) | 100% | ~49,000 |
| io_uring | 86.3% loss (~55k pps) | 100% | ~55,000 |
| mmsg | 84.7% loss (~61k pps) | 100% | ~61,000 |
| **mmsg + UDP GSO** | **0.09% loss (400k pps)** | **43%** | **~930,000** |
| AF_PACKET rings (tx unverified) | 0.00% loss (400k pps) | 30% | (see below) |

GSO is not an incremental win: it forwards the full 400k offered at under
half a core where mmsg saturates a core at ~61k, roughly 15x the work per
core, and it holds 0.06-0.09% loss from 100k to 400k. Verified end to end:
the sink's counter confirms delivery. Unthrottled it reaches ~960k pps at
100% CPU. Mechanism: one sendmsg carries a packed batch plus a UDP_SEGMENT
cmsg, so the stack is traversed once per batch instead of once per packet;
the receive side is still plain recvmmsg, so a GRO receive path would be
the next step.

**AF_PACKET numbers are NOT publishable as forwarding results.** Its rx
path demonstrably works (parses 100% of frames at 8-42% CPU), but frames
written to the TPACKET_V2 tx ring never reach the sink on loopback: the
kernel accepts the kick (send() returns success) and the sink receives
zero. Counting a forward is not proof of delivery, which is exactly why
the harness grew a sink column. Open: diagnose tx injection (candidate
causes: loopback packet-type classification of injected frames, or a
frame the stack accepts and then drops above the driver). Until then,
AF_PACKET is an rx-side result only.

## AF_PACKET transmit: diagnosis so far (still open)

Symptom: the engine receives and forwards correctly by its own counters,
but a UDP sink on the destination port receives nothing.

Established by experiment:
- Frames are built and transmitted correctly. A second AF_PACKET instance
  sniffing the destination port sees every frame, and with the
  PACKET_OUTGOING filter applied it still sees them, so they are genuinely
  looped back and observed as inbound at the tap.
- They enter the IP receive path: /proc/net/snmp Ip.InReceives rises by
  the injected count.
- They are not malformed at L3: InHdrErrors, InCsumErrors, InAddrErrors,
  InDiscards all stay flat.
- They are never delivered: Ip.InDelivers does not move, Udp.NoPorts does
  not move. So the drop is between ip_rcv_core and ip_local_deliver, i.e.
  in the input route lookup, which drops martians with no SNMP counter.

Mechanism (best current explanation): normal loopback traffic never takes
that path. Locally generated packets carry the dst entry from the output
route, so ip_rcv_finish skips ip_route_input entirely. An L2-injected
frame has no dst, so it takes the full input-routing path, where a
loopback source address is treated as martian.

Fixes tried, none sufficient: binding the tx socket to ETH_P_IP rather
than 0 (skb->protocol is stamped from the bound protocol, and
ip_route_input_slow treats protocol != ETH_P_IP as martian source);
enabling net.ipv4.conf.lo.route_localnet.

Remaining candidates: netfilter/conntrack rules in the WSL2 host-network
namespace, or a martian-source verdict that route_localnet does not cover.
Next step when resumed: enable net.ipv4.conf.all.log_martians and read
dmesg, which names the rejected source directly; or test on a veth pair
with non-loopback addressing instead of lo.

Real bugs found and fixed while chasing this:
- sockaddr_ll sits at offset 48 in a tpacket3 frame (TPACKET_ALIGN of the
  48-byte header), not 40; the PACKET_OUTGOING filter was reading a
  garbage byte and never filtering.
- The tx kick's return value was unchecked, so a rejected batch could pass
  silently. Now throws on anything but EAGAIN.
- The tx socket must be bound to ETH_P_IP for skb->protocol to be correct.

Verdict for the article: AF_PACKET belongs with XDP in the raw-frame rung,
and its numbers stay unpublished until a sink confirms delivery.

### What DPDK's AF_PACKET driver does, and what it means for ours

DPDK's af_packet PMD uses the same primitives (PACKET_MMAP rings, poll
tp_status on rx, fill + TP_STATUS_SEND_REQUEST + sendto kick on tx) plus
two things ours lacked:

1. **PACKET_QDISC_BYPASS**: without it every injected frame traverses the
   traffic-control layer (qdisc enqueue/dequeue plus its lock) on the way
   to the driver; with it the kernel calls dev_direct_xmit. This is the
   "socket queue overhead" question's actual answer, and it is one
   setsockopt. Now implemented.
2. **PACKET_FANOUT** to spread rx across sockets/threads, since one socket
   is one ring. Not needed for our single-threaded design yet.

The more important lesson is architectural: a DPDK application owns the
port and transmits **to the wire toward another host**. It never asks the
local kernel to route an injected frame back to a local socket. Our
loopback test did exactly that, which is why it hit the martian-source
path: locally generated packets carry a dst entry from their output route
and skip input routing entirely, while an injected frame has no dst, takes
the full input path, and is rejected.

So the fix is topological, not a cleverer injection: raw-frame forwarding
must be measured host-to-host (or across a veth pair spanning two network
namespaces), exactly like the LAN benchmark the rest of this project uses.
Implemented for that: PACKET_QDISC_BYPASS, configurable source/destination
MAC (AFPACKET_SRC_MAC / AFPACKET_DST_MAC) and source IP (AFPACKET_SRC_IP),
since a real link needs real addressing where loopback tolerated zeros.

Still to do: a test bed. The race container cannot build the two-namespace
topology (ip netns needs mount privileges podman does not grant here, and
/sys is not namespace-aware under nsenter). Options: run the peer side as
a second container on a podman network, or measure it on the real LAN with
the forwarder on a Linux host and the NAS as peer.

## AF_PACKET on the real LAN: delivery verified

Topology that finally worked: WSL2 switched to mirrored networking
(networkingMode=mirrored in .wslconfig), which gives the Linux VM the
workstation's real LAN address (192.168.178.143 on eth0) and therefore
L2 adjacency with the NAS. The forwarder runs there as a self-contained
single-file linux-x64 binary (podman's Windows client cannot reach its VM
under mirrored networking; containers were unnecessary anyway). The NAS
generates and sinks over the control API.

Also required: a Hyper-V firewall rule. Under mirrored networking, WSL
traffic is governed by the Hyper-V firewall, whose DefaultInboundAction
is Block, and which is separate from the ordinary Windows Firewall rules.
Without an inbound UDP 5000 rule there the forwarder sees nothing at all.

The engine self-configures: it reads its own MAC and IPv4 from the named
interface and resolves the peer's MAC from the neighbour table after an
ARP probe (resolved the NAS as 00:11:32:EA:20:7D), so a deployment only
names the interface.

| Offered | Sent | Forwarder rx | Rx loss | End-to-end loss |
| ------- | ---- | ------------ | ------- | --------------- |
| 100,000 | 999,922 | 999,922 | 0.00% | 1.67% |
| 200,000 | 1,999,915 | 1,999,915 | 0.00% | 12.92% |
| 300,000 | 2,997,874 | 2,685,367 | 10.42% | 38.41% |

The raw-frame path receives everything offered up to 200k pps and starts
shedding around 300k. End-to-end loss exceeds receive loss because the
return leg lands on the NAS sink, which ceilings near 220k pps; those are
sink-side drops, not forwarder drops (the forwarder's own drop counter
stayed at zero throughout).

Caveats: this runs on a mirrored WSL2 virtual adapter, a third environment
distinct from both the Windows-native rungs and the container-loopback
Linux race, so the numbers are not comparable across those sets. CPU per
packet was not captured: reading /proc across the wsl.exe boundary proved
unreliable. Next: run the forwarder on a native Linux host (the NAS, with
the workstation as peer) for numbers comparable to the rest of the ladder.

## Can rung 5 (XDP) even be measured on this hardware?

Checked rather than assumed:

- **Windows / Realtek PCIe 2.5GbE**: XDP-for-Windows native mode needs the
  NIC driver to implement Microsoft's NDIS XDP extensions (Intel, NVIDIA,
  Microsoft adapters in practice). A consumer Realtek driver does not ship
  them. Only generic mode would attach, and generic mode re-enters the
  stack XDP exists to bypass: not publishable as an XDP result.
- **Linux in the WSL VM**: eth0 is hv_netvsc (kernel 6.6), which does
  implement the XDP hook, so a program would attach in driver mode. But
  the packet reaches netvsc only after the Realtek driver, the Windows
  NDIS path and the Hyper-V switch have already handled it. XDP there
  bypasses the *Linux* stack while every cost XDP is meant to remove has
  already been paid upstream. Valid as an A/B of XDP vs sockets inside
  that VM; invalid as a measurement of what XDP does on real hardware.
  (netvsc also lacks AF_XDP zero-copy, so the umem would be copy-mode.)
- **This workstation under Linux**: the same NIC binds to r8169, which
  does not implement XDP either.

Consequence: a genuine rung 5 needs bare-metal Linux with an XDP-capable
driver. Best candidate in the house is the NAS if its NIC is Intel-based
(igb/igc/e1000e/ixgbe/i40e), with the workstation as peer. Otherwise the
honest outcome is to write rung 5 as a documented limitation: the rung
requires hardware support that commodity gear does not provide, which is
itself the deployment reality check the chapter promised.

Trap to guard against: AF_XDP silently falls back to generic/SKB mode on
unsupported drivers. Any run must assert it got the mode it asked for
(XDP_FLAGS_DRV_MODE) rather than accept a plausible-looking number.

### Verdict: no XDP-capable NIC available

NAS interfaces report driver r8168 (Realtek's out-of-tree driver) on both
eth0 and eth1. Neither r8168 nor the in-kernel r8169 implements the XDP
hook, and the workstation's Realtek 2.5GbE has no NDIS XDP extensions on
Windows. Every candidate in the environment is therefore generic-mode
only, which is not a publishable XDP result.

Rung 5 options:
1. Write it as a documented limitation, backed by this evidence. The rung
   requires driver support commodity hardware does not provide.
2. Unlock it with hardware: an Intel-based NIC (igc/igb/i350/X520 class)
   is inexpensive and gives native XDP on Linux.
3. Unlock it in the cloud: AWS ENA and GCP gVNIC implement native XDP, so
   two instances would give a genuine driver-mode measurement in an
   environment where both peers are equally virtualized.

AF_PACKET keeps its place in this rung precisely because it needs none of
that: raw frames, no eBPF, no driver support, and it is already verified
forwarding on a real link here.

## Linux engines over the real LAN (WSL VM, mirrored adapter)

Same peer (NAS generator + sink), same 10 s runs, 4 sender threads.
Receive loss is sender count vs the forwarder's own counter; end-to-end
loss includes the return leg into the NAS sink, which ceilings near
220k pps, so above that it measures the sink, not the forwarder.

| Engine | 200k offered rx loss | 300k offered rx loss |
| ------ | -------------------- | -------------------- |
| mmsg   | 2.37% | 5.72% |
| mmsg + GSO | 0.32% | **1.04%** |
| AF_PACKET | **0.00%** | 10.15% |

Reading: GSO is the most robust across the range, holding ~1% loss at
300k where plain mmsg sheds 5.7%. AF_PACKET is flawless to 200k and then
falls off a cliff, which fits its design here: a single-threaded engine
doing all header work itself, with no kernel batching to fall back on
once the ring pressure rises.

Note the environment: this is the mirrored WSL2 virtual adapter, so these
numbers are comparable to each other but not to the Windows bare-metal
rungs, and not to the container-loopback race (where GSO reached
~930k pps/core because loopback has no NIC in the path).

## Linux engine CPU over the real LAN (self-reported)

CPU capture fixed by making every forwarder report its own
Process.TotalProcessorTime per stats interval (immune to the /proc-across-
wsl.exe problems; identical accounting on every OS). At a sustained
200,000 pps over the LAN, steady-state, zero loss and zero drops:

| Engine | CPU (one core) at 200k pps |
| ------ | -------------------------- |
| mmsg | ~35-39% |
| mmsg + GSO | ~26% |
| AF_PACKET rings | ~13% |

Within-Linux this is a clean quantitative ranking: GSO ~1.4x cheaper than
mmsg, AF_PACKET ~2.7x cheaper than mmsg at fixed load. It also closely
matches the loopback race's GSO/AF_PACKET figures (28%/16% at 200k),
which cross-validates the two environments for those engines.

Cross-OS caveat, stated once and honestly: process CPU excludes work the
kernel does outside the process. On Linux, receive softirq processing is
not billed to the process, and AF_PACKET benefits most from that (the
kernel fills its ring in softirq context; the app only reads it), so its
13% understates total system cost more than the socket engines' numbers
do. Windows bills DPC work outside the process similarly. Same-OS
comparisons are solid; cross-OS ones are indicative only. With that
caveat: the Linux engines at 200k use less app-side CPU than Windows RIO
(63%) despite running behind the Hyper-V switch, a one-sided bound in
their favor since the virtualization handicap only inflates their cost.

Odd datum for completeness: mmsg does 200k at ~36% here but capped at
~61k/core in the container-loopback race. Arrival batching differs
(NIC-coalesced bursts batch recvmmsg efficiently; loopback wakes per
packet), another instance of the arrival-pattern lesson.

## Rust + RIO: the wins stack, exactly as predicted

Prediction on record before the port: language delta measured ~5% on
identical architecture, so Rust+RIO should land near 60% at 200k against
C# RIO's 63.1%. Measured (same harness, same NIC-hygiene config, 8-thread
batched generator, 10 s runs, warmed):

| Offered | Rust RIO rx loss | Rust RIO CPU | C# RIO (loss / CPU) |
| ------- | ---------------- | ------------ | ------------------- |
| 150,000 | 0.00% | 47.5% | 0.00% / 50.8% |
| 200,000 | 0.00% | 58.0% | 0.28% / 63.1% |
| 250,000 | 0.00% | 78.8% | 1.81% / 80.5% |
| 300,000 | 0.00% | 89.2% | 6.41% / 88.6% |
| 350,000 | 4.04% | 98.4% | - |

58.0% measured vs ~60% predicted: the effects are additive, no compounding.
Rust+RIO is also cleaner at the top: 0.00% loss at 300k where C# RIO shed
6.41%, intake holding to ~336k/s at 350k offered. New best on the ladder,
by the margin the model said it would be and no more. The ordering stands
confirmed by construction: interface first (25-30%), dispatch model second
(10-15%), language last (~5%).

### Fairness check: SO_RCVBUF on the RIO rungs

The Rust RIO port sets SO_RCVBUF to 1 MB; the C# RIO engine did not.
Aligned and re-measured: C# RIO with the 1 MB buffer reads 0.00%/65.2%
at 200k and 8.15%/88.9% at 300k, within run variance of the published
0.28%/63.1% and 6.41%/88.6%. As theory predicts for RIO (posted receives
are the buffering; the socket buffer is bypassed), the option is a no-op,
so the Rust-vs-C# comparison was already fair. The setting stays in both
engines so the ladder's alignment is uniform by construction rather than
by argument.

## Can Linux and Windows be compared? Same C# code, both OSes

The one portable comparison: identical Forwarder.Rung2.Frugal (plain .NET
sockets) at 200k pps, Windows bare metal vs Linux (WSL mirrored adapter).

| Dispatch | Windows (bare metal) | Linux (virtualized) |
| -------- | -------------------- | ------------------- |
| blocking (sync) | 80% of one core | ~43% |
| async (epoll/IOCP) | 88% | ~131% (1.3 cores) |

Two findings, one correction:

1. **The OS effect is not single-signed.** Same code: Linux is far
   CHEAPER blocking (43 vs 80) and far MORE EXPENSIVE async (131 vs 88).
   So "the kernel has the biggest effect" cannot be stated cleanly - the
   sign flips with the dispatch model. And it is confounded two ways that
   pull opposite directions: virtualization inflates the Linux cost, while
   softirq receive processing (unbilled to the Linux process) deflates it.

2. **CORRECTION to the earlier section.** I wrote that the Linux engines'
   lower app CPU vs Windows RIO was "a one-sided bound in their favor."
   That was wrong: it ignored the softirq exclusion, which deflates the
   Linux number, so the cross-OS figures are not a clean bound in either
   direction. Same-OS rankings stand; cross-OS ones do not.

3. **.NET async sockets on Linux are genuinely bad for this workload**:
   1.3 cores to forward 200k pps, 3x the blocking path, matching the
   container race where the epoll engine collapsed. This IS a clean
   within-Linux result (process CPU, same environment).

## The hierarchy, stated honestly

- Interface, dispatch, language CAN be ranked (measured on identical
  hardware within each OS): interface ~25-30% > dispatch ~10-15% (up to 3x
  on Linux async) > language ~5%.
- The OS/kernel CANNOT be added as a clean 4th axis, for two reasons:
  (a) the fast interfaces ARE the kernel - RIO is Windows-only, io_uring/
  GSO/XDP are Linux-only, so "which kernel" and "which interface" are the
  same choice; you can only hold interface constant with plain BSD sockets;
  (b) that one portable comparison is confounded and sign-flipping (above).
- So the real top of the hierarchy is a single fused axis, "the kernel
  interface," then dispatch model, then language.
