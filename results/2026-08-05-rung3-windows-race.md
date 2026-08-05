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
Three runs: Defender ON + Realtek driver 1125.26.50.2025; Defender OFF +
same driver; Defender OFF + driver 10.80.20.407 (published set, below).
NIC hygiene re-audited and restored after the driver update (it reset
ReceiveBuffers/GreenEthernet/GigaLite; see ENVIRONMENT.md).

Defender OFF, driver 10.80.20.407 (the published race):

| Offered | rio | rio-defer | uso | uso+uro-32 |
| ------- | --- | --------- | --- | ---------- |
| 150,000 | 0.00% / 38.8% | 0.00% / 37.4% | 0.13% / 17.1% | - |
| 200,000 | 0.00% / 55.5% | 0.00% / 49.8% | 0.01% / 28.0% | 0.00% / 60.8% |
| 250,000 | 0.00% / 77.8% | 0.00% / 57.3% | 0.00% / 35.5% | - |
| 300,000 | 0.10% / 85.3% | 0.00% / 70.0% | 0.00% / 35.5% | 0.43% / 92.7% |
| 400,000 | - | 0.47% / 91.2% | 1.29% / 52.3% | 15.61% / 98.9% |

**Driver A/B**: the 10.80 driver left the socket/ring engines within
variance of the 1125 driver (rio 54.1 -> 55.5, defer 46.6 -> 49.8 at
200k) but improved USO by ~9 points everywhere (37.3 -> 28.0 at 200k,
51.6 -> 35.5 at 300k, 76.0 -> 52.3 at 400k). Not hardware USO:
`Get-NetAdapterUso` still lists nothing, so segmentation stays in the
stack; the driver just services the large segmented NBL send path more
cheaply. At 300k, USO is now 2.4x cheaper than per-request RIO.

Defender OFF, driver 1125.26.50.2025:

| Offered | rio | rio-defer | uso | uso+uro-32 |
| ------- | --- | --------- | --- | ---------- |
| 150,000 | 0.14% / 45.1% | 0.00% / 33.8% | 0.00% / 24.9% | - |
| 200,000 | 0.00% / 54.1% | 0.00% / 46.6% | 0.00% / 37.3% | 0.00% / 60.6% |
| 250,000 | 0.00% / 74.5% | 0.00% / 62.2% | 0.00% / 41.9% | - |
| 300,000 | 0.03% / 84.1% | 0.00% / 67.0% | 0.00% / 51.6% | 0.00% / 93.3% |
| 400,000 | - | 0.29% / 90.1% | 0.33% / 76.0% | 17.87% / 99.5% |

Defender ON, driver 1125.26.50.2025 (kept for the Defender A/B):

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
   batch) save 10-20% of the engine** (published set: 55.5 -> 49.8 at
   200k, 85.3 -> 70.0 at 300k), clean through 300k, intake ~400k at 0.5%
   loss. The engine change is a flag on RIOReceiveEx/RIOSendEx plus two
   commit calls. This is transition batching (the mmsg shape) done inside
   RIO: request insertion becomes a pure user-mode ring write.
2. **USO (UDP_SEND_MSG_SIZE) halves the engine and keeps going**: 28.0%
   vs 55.5% at 200k, 35.5% vs 85.3% at 300k (2.4x), and 52.3% at 400k
   while forwarding 98.7% of offered. Same family win as Linux GSO, now
   measured on Windows, from a plain blocking socket loop (~100 lines of
   ordinary C#, two setsockopts, no interop). Send path: pack up to 64
   equal-size payloads, one send per batch, stack segments once per batch
   (loopback smoke confirmed true re-segmentation: 500 packed payloads
   arrive as 500 distinct 32 B datagrams). The ordering held in every
   configuration tried (Defender on/off, both drivers).
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
   Late addendum (same day): the strongest-case probe also came back
   empty. Wire traffic (not loopback, whose synchronous per-send
   delivery may never present a coalescible batch, weakening the
   original loopback argument), single flow, 1200 B payloads, a 3 s
   deliberately backlogged 4 MB socket queue drained through raw
   WSARecvMsg with UDP_COALESCED_INFO control space and msquic's exact
   option value (65527): 295,511 receives, all exactly 1200 B, zero
   coalesced cmsgs. Also checked: Get-NetUDPSetting and
   Get-NetOffloadGlobalSetting expose no URO knob (RSC there is TCP);
   no Tcpip\Parameters registry values match Uro/Coalesc.
   Conclusion: Get-NetAdapterUro only rules out the HARDWARE half; the
   software half is ruled out behaviorally, at its best case.

   CAUSE INVESTIGATION (2026-08-05, later): msquic's troubleshooting
   guide documents software URO as a real feature and lists why it goes
   dark: receive-offload state disabled (ours: enabled), PTP
   timestamps, WFP callouts/IPSNPI clients setting a global disable
   mask, and incompatible NDIS drivers on the interface. Npcap's own
   tracker (nmap/npcap#737, #70) states NDIS disables URO (and RSC)
   when an LWF targeting an older NDIS version is bound; npcap was
   bound to every adapter here, and WLAN 4 shows the matching RSC
   failure reason NDISCompatibility. TESTED: unbound npcap from
   Ethernet 9 -> Get-NetAdapterUro still empty, RSC still not
   enumerated, and the wire probe still returned 290,399 receives of
   exactly 1200 B with zero coalesced cmsgs. So npcap alone is not the
   cause. Still bound and untested individually: vmware_bridge,
   MS_NDISPROT (HTC), ms_l2bridge (the WSL mirrored-networking bridge),
   ms_l1vhlwf. Definitive attribution needs a TCPIP Full.Verbose trace
   (look for "URO SCU received. SegCount=..."), not user-space probing.
   SOLVED (2026-08-05, TCPIP Full.Verbose trace, bench/uro-trace.ps1):

     Framing: interface rundown: Interface = 5, Alias = Ethernet 9,
       SW RSC/URO applicable = 1(TRUE), SW RSC enabled = 1(TRUE),
       SW URO enabled = 1(TRUE).
     TCP software RSC global disabled mask = 0,
       UDP software URO global disabled mask = 48.

   The interface was capable AND enabled the whole time. URO is killed
   by a GLOBAL disable mask of 48. msquic's TSG decodes the values:
   mask 2 = an incompatible WFP callout; mask 48 (0b110000) = an
   incompatible IPSNPI client, namely winnat or FSE, which are enabled
   automatically when WSL or Hyper-V are enabled on the machine.
   Verified present here: winnat service Running, and the adapter
   "vEthernet (FSE HostVnic)" (Hyper-V Virtual Ethernet Container
   Adapter). Both exist because this box hosts the WSL2 VM that ran
   every Linux measurement in this project.

   => The Linux test rig disabled the Windows feature under test.

   Eliminated along the way (both tested, both negative): npcap
   unbound (nmap/npcap#737 mechanism), and the adapter stripped to
   bare IPv4 (every other NDIS filter disabled + adapter reset).
   Get-NetAdapterUro is the wrong instrument entirely: it reports
   HARDWARE URO, which the RTL8125 lacks, and says nothing about the
   software feature. TCP RSC was coalescing in the same trace
   (RSC = 1(TRUE) CoalescedSegCount = 2), its mask being 0.

   RUNTIME CLEARING DOES NOT WORK (tested, bench/fix-uro.ps1 -Apply):
   with winnat stopped AND both Hyper-V vnics (Default Switch, FSE
   HostVnic) disabled, a fresh trace still reads mask = 48 and the
   probe still coalesces nothing (300,000 receives, all 1200 B). The
   IPSNPI clients register with tcpip at boot; stopping the service and
   disabling the adapters afterwards does not retract the registration.
   Clearing it therefore needs the Hyper-V/WSL features themselves
   disabled plus a reboot, or a machine that never had them.

   CONFIRMED BY REMOVAL (2026-08-05): Hyper-V + VirtualMachinePlatform +
   WSL uninstalled, reboot. Mask went 48 -> 0 and coalescing began
   immediately on the SAME Realtek NIC whose Get-NetAdapterUro reports
   nothing, which settles that hardware capability was never the issue.
   Same 360,000,000 bytes over the wire, before vs after:
     mask 48: 300,000 receives, all 1200 B, 0 coalescing cmsgs
     mask  0:  46,905 receives (-84% syscalls), 45,259 cmsgs,
               253,096 datagrams delivered as passengers, receive sizes
               up to 33,600 B (28 datagrams in one receive)

   NDIS FILTERS EXONERATED (bench/fix-uro.ps1 -Bisect, mask 0, each
   filter enabled in turn against live traffic): INSECURE_NPCAP,
   vmware_bridge, MS_NDISPROT, ms_l2bridge, ms_l1vhlwf and ms_pacer are
   ALL harmless here; coalescing continued with every one of them bound.
   Npcap in particular contradicts nmap/npcap#737, which describes older
   builds pinning a low NDIS version; current Npcap negotiates at
   runtime. So the sole cause on this machine was the virtualization
   stack, and the filter list is a historical suspect list only.

   LOOPBACK CANNOT TEST URO: with mask 0, a loopback run still coalesces
   nothing (synchronous per-send delivery presents no batch to merge).
   Every early loopback negative was therefore uninformative, not proof.

   PUBLISHED CLAIM: software URO is real, was enabled and applicable on
   this interface all along, and was vetoed system-wide by a
   virtualization feature with no API surfacing the fact. Uninstalling
   Hyper-V/VMP/WSL plus a reboot is the only thing that cleared it.
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
- USO has a documented kernel software fallback when the NIC lacks
  hardware support (Intel adapter guide 29.2, "UDP Segmentation Offload";
  matches our measurement: works on Realtek with Get-NetAdapterUso
  empty). Every segmentation number in this project is SOFTWARE
  segmentation: no hardware USO on the Realtek (either OS), hv_netvsc
  does not forward NETIF_F_GSO_UDP_L4, and the container race was
  loopback (no NIC). The GSO/USO figures are therefore the family's
  floor; hardware-capable NICs would widen the gap.
- Still owed: whether USO composes with RIO rings; URO on a NIC that
  supports it; io_uring multishot rematch on Linux.
