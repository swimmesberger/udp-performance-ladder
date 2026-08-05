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

| Offered | rio | rio-defer | uso | uso+uro-32 |
| ------- | --- | --------- | --- | ---------- |
| 150,000 | 0.10% / 42.2% | 0.00% / 45.0% | 0.00% / 26.2% | - |
| 200,000 | 0.00% / 66.3% | 0.00% / 52.4% | 0.00% / 31.1% | 0.00% / 71.7% |
| 250,000 | 0.03% / 80.7% | 0.00% / 71.4% | 0.00% / 40.1% | - |
| 300,000 | 0.22% / 95.8% | 0.00% / 80.5% | 0.00% / 51.5% | 3.17% / 99.7% |
| 400,000 | - | 1.86% / 98.1% | 1.36% / 59.0% | 27.57% / 100.1% |

## Findings

1. **Deferred commits (RIO_MSG_DEFER + one RIO_MSG_COMMIT_ONLY per dequeue
   batch) save ~a fifth of the engine**: 66.3% -> 52.4% at 200k, clean
   through 300k where per-request rio starts shedding, intake ~390k. The
   engine change is a flag on RIOReceiveEx/RIOSendEx plus two commit calls.
   This is transition batching (the mmsg shape) done inside RIO: request
   insertion becomes a pure user-mode ring write.
2. **USO (UDP_SEND_MSG_SIZE) halves the whole engine**: 31.1% at 200k vs
   the ring engine's 66.3%, and 59.0% at 400k while forwarding 98.6% of
   offered. Same regime change as Linux GSO, now measured on Windows, from
   a plain blocking socket loop (~100 lines of ordinary C#, two
   setsockopts, no interop). Send path: pack up to 64 equal-size payloads,
   one send per batch, stack segments once per batch (loopback smoke
   confirmed true re-segmentation: 500 packed payloads arrive as 500
   distinct 32 B datagrams).
3. **URO (UDP_RECV_MAX_COALESCED_SIZE) never engaged**: the option is
   accepted (24H2+) but receives stayed one datagram each on this Realtek
   rig, so the uro engine variant (which forwards each coalesced blob as
   one batch) degenerated to per-packet sends and performed like the plain
   blocking loop (71.7% at 200k). The negative result is "no driver
   support, no software coalescing on this path", not "URO harmful".
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
