# Rung 3 Windows race: per-request RIO vs deferred commits vs USO

Date: 2026-08-05. New engines in `Forwarder.Rung3.Batched` (`--engine
rio|rio-defer|uso`, `--uro-segment <n>`), measured back to back with
`bench/matrix-rung3.sh`.

## Measurement caveats (both matter)

1. **Busier machine than the published runs**: the workstation was running
   a dev server, a browser preview, and an agent session. Absolute numbers
   do not compare to the published rung 3 table (63.1% @ 200k); the rio
   column below is the same engine re-measured as the same-day anchor.
2. **New CPU statistic**: `measure-windows.sh` previously read the last 3
   stats lines, which land in the idle tail after the sender stops (that
   bug produced "cpu 0.0%"). It now takes the median of loaded samples
   (rx > 1000 pps), dropping two ramp seconds and the partial last second.
   Median reads lower than the mid-run manual reads used earlier.

## The matrix (32 B, bursty, 8 sender threads, 10 s runs, warmed)

CPU = median loaded sample, one core. Loss = sender vs forwarder rx.
Two runs: first with Defender real-time protection + firewall ON, then
(same day, published set) with both OFF.

Defender OFF (the published race):

| Offered | rio | rio-defer | uso | uso+uro-32 |
| ------- | --- | --------- | --- | ---------- |
| 150,000 | 0.14% / 45.1% | 0.00% / 33.8% | 0.00% / 24.9% | - |
| 200,000 | 0.00% / 54.1% | 0.00% / 46.6% | 0.00% / 37.3% | 0.00% / 60.6% |
| 250,000 | 0.00% / 74.5% | 0.00% / 62.2% | 0.00% / 41.9% | - |
| 300,000 | 0.03% / 84.1% | 0.00% / 67.0% | 0.00% / 51.6% | 0.00% / 93.3% |
| 400,000 | - | 0.29% / 90.1% | 0.33% / 76.0% | 17.87% / 99.5% |

Defender ON (earlier same-day run, kept for the A/B):

| Offered | rio | rio-defer | uso | uso+uro-32 |
| ------- | --- | --------- | --- | ---------- |
| 150,000 | 0.10% / 42.2% | 0.00% / 45.0% | 0.00% / 26.2% | - |
| 200,000 | 0.00% / 66.3% | 0.00% / 52.4% | 0.00% / 31.1% | 0.00% / 71.7% |
| 250,000 | 0.03% / 80.7% | 0.00% / 71.4% | 0.00% / 40.1% | - |
| 300,000 | 0.22% / 95.8% | 0.00% / 80.5% | 0.00% / 51.5% | 3.17% / 99.7% |
| 400,000 | - | 1.86% / 98.1% | 1.36% / 59.0% | 27.57% / 100.1% |

**Defender A/B**: disabling real-time protection + firewall saved the
per-packet engines ~10-12 points of a core at 200k+ (rio 66.3 -> 54.1,
uro-variant 71.7 -> 60.6) while barely moving USO (31.1 -> 37.3, within
run variance in the other direction): the filtering stack is itself a
per-packet cost, and batched sends dodge most of it. Loss also cleaned
up everywhere (defer 1.86% -> 0.29% at 400k, uso 1.36% -> 0.33%).

## Findings

1. **Deferred commits (RIO_MSG_DEFER + one RIO_MSG_COMMIT_ONLY per dequeue
   batch) save 15-20% of the engine** (Defender off: 54.1 -> 46.6 at
   200k, 84.1 -> 67.0 at 300k), clean through 300k, intake ~400k at 0.29%
   loss. The engine change is a flag on RIOReceiveEx/RIOSendEx plus two
   commit calls. This is transition batching (the mmsg shape) done inside
   RIO: request insertion becomes a pure user-mode ring write.
2. **USO (UDP_SEND_MSG_SIZE) takes another third off**: 37.3% vs 54.1% at
   200k, 51.6% vs 84.1% at 300k, and 76.0% at 400k while forwarding 99.7%
   of offered. Same family win as Linux GSO, now measured on Windows, from
   a plain blocking socket loop (~100 lines of ordinary C#, two
   setsockopts, no interop). Send path: pack up to 64 equal-size payloads,
   one send per batch, stack segments once per batch (loopback smoke
   confirmed true re-segmentation: 500 packed payloads arrive as 500
   distinct 32 B datagrams). Under Defender the gap was larger still
   (31.1 vs 66.3): the per-packet engines pay the filtering tax, USO
   mostly does not.
3. **URO (UDP_RECV_MAX_COALESCED_SIZE) never engaged, and this is now
   diagnosed, not guessed.** The option is accepted, and
   `netsh int udp show global` reports "Receive Offload State: enabled",
   but `Get-NetAdapterUro` lists no capable adapter (no hardware URO on
   the Realtek), and a histogram probe (scratchpad uroprobe: bind, opt in
   64 KB coalescing, count receive sizes under generator load) came back
   100% single-datagram receives in every condition tried:
   - 8 interleaved flows, 32 B, 200k pps (the ladder's profile)
   - a single flow (perfectly consecutive, coalescing-eligible), 32 B
   - a single flow of QUIC-sized 1200 B datagrams (URO's design target)
   - the WSARecvMsg receive path instead of plain recv
   - loopback (no driver in the path at all)
   - a saturated receiver at 500k pps offered (backlogged socket queue)
   Conclusion: on this build (26200), the net-offloads spec's "existing
   software URO feature" does not mean a general software coalescer for
   opted-in sockets; in practice URO delivers coalesced blobs only where
   the layer below already batches (hardware URO / virtualized paths).
   URO on commodity hardware = hardware feature waiting for hardware.
   Receive-side batching remains the open gap on Windows: recv syscalls
   are still per-packet in the uso engine (its 400k loss at 59% CPU is a
   receive-side cliff, not a CPU cliff).
4. **Windows now mirrors Linux, family for family**: stack batching
   (USO/GSO) > transition batching (defer/mmsg) > per-request ring calls.
   The interface hierarchy is not an OS quirk.

## API notes for the article

- Windows has no recvmmsg/sendmmsg. Nearest relatives: TransmitPackets
  (multi-datagram send on a connected UDP socket, kernel worker serviced;
  not raced here since USO covers the send side better) and
  GetQueuedCompletionStatusEx (batch completion dequeue for classic IOCP).
- RIO_MSG_DEFER inserts a request without kicking the kernel;
  RIO_MSG_COMMIT_ONLY (all other args NULL/zero) kicks everything
  deferred on the queue. Both receives and sends support it.
- UDP_SEND_MSG_SIZE (USO) = Windows 10 2004+; UDP_RECV_MAX_COALESCED_SIZE
  (URO) = Windows 11 24H2+, driver-dependent in practice.
- Still owed: whether USO composes with RIO rings; URO on a NIC that
  supports it; io_uring multishot rematch on Linux.
