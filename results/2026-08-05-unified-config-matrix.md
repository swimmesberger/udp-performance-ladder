# The unified-configuration matrix (every engine, one config)

Date: 2026-08-05. Every number in this file (and now in the article) comes
from ONE configuration, so every table compares to every other:

- Defender real-time protection + firewall OFF, Realtek driver
  10.80.20.407, NIC hygiene per ENVIRONMENT.md (re-applied post-update).
- CPU statistic: median of loaded stats-line samples (2 ramp seconds and
  the partial last second dropped), self-reported by each forwarder with
  identical accounting (GetProcessTimes / Process.TotalProcessorTime).
- 32 B payloads, bursty batched generator, 10 s runs, 3 s discarded
  warmup, loss = sender count vs the forwarder's own rx counter.
- Session-loaded machine (dev server etc.): absolute numbers run a little
  hot; comparisons within the set are the claim.

## Windows (bare metal, 1 GbE)

CPU at offered rate (% of one core) / rx loss where notable:

| Engine | 150k | 200k | 250k | 300k | beyond |
| --- | --- | --- | --- | --- | --- |
| rung1 naive UdpClient (async) | 74.4 | 76.6 | 94.1 (3.8%) | 98.5 (11.2%) | |
| rung2 frugal Socket (async) | 68.0 | 86.2 | 89.0 (5.3%) | 99.6 (10.5%) | |
| rung2 frugal (--sync) | 49.9 | 63.1 | 91.5 | 98.8 (11.5%) | |
| rust std blocking | 53.1 | 65.6 | 78.1 | 90.6 | |
| rust tokio (current_thread) | 45.3 | 59.3 | 76.5 | 95.3 | |
| C# RIO per-request | 38.8 | 55.5 | 77.8 | 85.3 | 400k: 99.6 (9.3%) |
| C# RIO deferred commits | 37.4 | 49.8 | 57.3 | 70.0 | 400k: 91.2 (0.5%) |
| rust RIO | 40.6 | 51.5 | 60.9 (1.7%) | 84.3 | 350k: 92.2 (0.1%) |
| C# USO | 17.1 | 28.0 | 35.5 | 35.5 | 400k: 52.3 (1.3%) |

## Linux over the real LAN (mirrored WSL adapter; same-table comparisons only)

| Engine | 200k | 300k |
| --- | --- | --- |
| plain async (.NET epoll) | 143.2 (3.3%) | - |
| plain blocking | 49.7 (1.2%) | - |
| mmsg | 43.6 (1.9%) | 55.2 (2.5%) |
| io_uring | 70.4 (0.2%) | 85.0 (3.2%) |
| io_uring + GSO | 71.2 (0.1%) | 86.1 (0.1%) |
| io_uring + GSO + GRO | 122.1 (0.0%) | 137.1 (0.0%) |
| mmsg + GSO | 27.8 (0.0%) | 36.1 (0.0%) |
| **mmsg + GSO + GRO** | **24.4 (0.00%)** | **32.7 (0.02%)** |
| AF_PACKET rings | 12.5 (0.0%) | 14.9 (14.3%) |

## The symmetric pairs: what the RECEIVE side is worth

Added after the main campaign, because the two OSes' "stack batching"
engines were not structural twins: the Windows USO engine receives one
datagram per syscall, while the Linux gso engine was also getting
recvmmsg batching for free. New lanes isolate the receive path
(`gso-plainrx`, `gso-gro-plainrx` on Linux; a fixed URO engine on
Windows that packs across receives instead of degenerating when
coalescing never happens).

Linux, real link, CPU @200k (all with GSO packed sends):

| Receive path | CPU @200k | CPU @300k |
| --- | --- | --- |
| one recvmsg per datagram | 47.8% | 64.3% (5.1% loss) |
| recvmmsg batching | 27.8% | 36.1% |
| one recvmsg + UDP_GRO | **25.8%** | - |
| recvmmsg + UDP_GRO | **24.4%** | 32.7% |

Windows, bare metal, CPU @200k (USO packed sends, one recv per datagram):

| Engine | CPU @200k | CPU @300k |
| --- | --- | --- |
| uso (send offload only) | 29.2% | 42.1% |
| uso + URO opt-in | 27.7% | 43.2% |

Two findings:

1. **GRO alone replaces recvmmsg.** Going from per-datagram receives to
   GRO coalescing saves 22 points (47.8 -> 25.8); going to recvmmsg
   saves 20 (47.8 -> 27.8). Adding mmsg on top of GRO buys only 1.4
   more, so the two are near-redundant: the receive twin does the
   batching job by itself, and does it slightly better, because a
   coalesced blob amortizes stack traversal as well as syscalls.
2. **USO + URO == USO on this hardware** (27.7 vs 29.2 @200k, 43.2 vs
   42.1 @300k: within run noise, sign flips by rate). Expected, since
   URO never coalesces here; what it proves is that the opt-in itself
   is free, so the earlier 60.6% "uso+uro" figure was an artifact of
   the old engine forwarding each receive as its own batch. That row is
   superseded; the URO opt-in is inert, not harmful.

Consequence for the article's asymmetry claim: it is not just that
Windows' URO is dark. Windows has NO receive-side batching available at
all (no recvmmsg equivalent, URO inert), so its receive path is
structurally pinned at one syscall per datagram, while Linux has two
independent remedies and either one suffices.

## Findings

1. **Family order confirmed under one config on both OSes**: stack
   batching > transition batching > rings > per-packet loops. Winners:
   USO 28.0 on Windows, mmsg+GSO+GRO 24.4 on Linux, both plain sockets.
2. **GRO works, in software, on Linux**: gso -> gso-gro saves ~12% CPU
   and cleans up loss. The receive twin is software on Linux only.
3. **URO is hardware-only on Windows, final**: the msquic-shaped probe
   (raw WSARecvMsg + UDP_COALESCED_INFO control space, correct GUID
   f689d7c8-6f1f-436b-8a53-e54fe351c322) received 300k datagrams on
   loopback with ZERO coalesced cmsgs. Combined with the earlier
   six-condition probe: no software URO exists for ordinary sockets.
4. **io_uring loses on the real link, worse than on loopback**: 70.4 vs
   mmsg 43.6 at 200k. Adding GSO to the ring (uring-gso) fixes
   robustness (0.1% at 300k) but not CPU (71.2): the ring's own overhead
   dominates. The "io_uring+GSO sweet spot" claim does not hold for this
   workload on this rig.
5. **io_uring + GRO cmsgs trips the worker pool: DIAGNOSED AND FIXED.**
   Unfixed, uring-gso-gro burned 122-137% CPU on a single-threaded
   engine. Mid-run thread census (bench/census-in-vm.sh): **12,739
   iou-wrk-* kernel worker threads** inside the process, vs exactly 1
   for uring-gso without control data. Mechanism: recvmsg carrying
   msg_control is punted off io_uring's polled fast path onto io-wq,
   whose pool is unbounded for network ops (Cloudflare's
   missing-manuals-io_uring-worker-pool post documents the pool
   anatomy; they saw 4,096 threads, one per in-flight request). Fix:
   IORING_REGISTER_IOWQ_MAX_WORKERS(1,1) at ring setup -> census reads
   1 worker, engine measures 63.1% @200k / 80.9% @300k at 0.04% loss.
   Post-fix, GRO helps the ring (63.1 < uring-gso 72.1) but every op
   still detours through the worker, so it stays 2.6x behind
   mmsg+GSO+GRO. The ring's fast path and offload cmsgs do not compose.
6. **Two Windows survival traps** (exposed by firewall-off + new driver;
   fixed in all engines, see commit d77424e): inbound ICMP
   port-unreachable kills a receive loop unless SIO_UDP_CONNRESET is
   disabled, and the 10.80 driver backpressures per-packet sends with
   fatal WSAENOBUFS at rates the 1125 driver absorbed (now a counted
   drop; rung1 at 150k drops ~0.15% on its own tx counter).
7. **Socket-rung shifts vs the Aug 3 published set**: the plain socket
   engines now break by 250k (was 300k) and the rung1-vs-rung2 delta
   bounces both directions by up to 10 points across rates (null result
   holds, with more noise). C# sync vs rust blocking is within noise
   (63.1 vs 65.6): the language delta is no longer resolvable at the
   socket rungs; on RIO it reads ~7% relative (55.5 -> 51.5).
8. **rio intake ceiling** under this config: ~362k (9.3% loss at 400k);
   rust RIO clean to 350k (0.08%); defer ~400k at 0.5%.
