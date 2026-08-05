# bench/

Everything needed to reproduce the measurements. Start with
[ENVIRONMENT.md](ENVIRONMENT.md): it holds the rig, the NIC hygiene the
numbers depend on, and the gotchas that cost real time to rediscover.

## Measuring

| Script | What it does |
| --- | --- |
| `measure-windows.sh` | Rate ladder against one Windows forwarder binary. The primitive every Windows campaign is built from. Loss = sender count vs the forwarder's own rx counter; CPU = median of a run's loaded seconds. |
| `measure-linux-vm.sh` | Same, for a Linux engine inside the WSL2 VM (`FWD_BIN` selects the binary, engine name selects the mode). |
| `spot-rung3.sh` | Two-engine sanity check at 200k, for after a driver or config change. |

## Campaigns

Each runs a set back to back so the columns compare to each other.

| Script | Set |
| --- | --- |
| `matrix-windows-all.sh` | The full Windows ladder: naive, frugal (async and sync), the three Rust engines, plus a RIO ceiling row. |
| `matrix-rung3.sh` | The Windows batching families: per-request RIO, deferred RIO, USO, USO+URO. |
| `matrix-linux-all.sh` | The nine-engine Linux grid over the real link, from plain sockets to AF_PACKET. |
| `ab-uro.sh` | Interleaved A/B (three rounds each, alternating) for the USO vs USO+URO pair. The protocol to copy whenever two engines are close enough that drift could fake a winner. |

## Diagnostics

| Script | What it answers |
| --- | --- |
| `census-in-vm.sh` | "Where is that CPU going?" Counts a Linux engine's threads mid-run; this is what caught io_uring's 12,739 io-wq workers. |
| `fix-uro.ps1` | "Why is software URO not coalescing?" Diagnose, temporarily remove the known blockers, restore exactly. See below. |
| `uro-trace.ps1` | Collects a verbose TCPIP trace around live traffic and greps it for the URO verdict. Elevated. |

## The URO workflow

Software URO can be off for reasons no API reports, and the thing most
people check first is the wrong question. Order of operations:

1. `fix-uro.ps1 -Diagnose` — bindings, IPSNPI clients, offload switch.
2. `uro-trace.ps1` (elevated) — read the two lines that matter:
   `SW RSC/URO applicable` / `SW URO enabled` on the interface, and
   `UDP software URO global disabled mask = N`.
3. Decode the mask: **0** healthy, **2** an incompatible WFP callout,
   **48** incompatible IPSNPI clients (winnat or FSE, switched on
   automatically by WSL and Hyper-V).
4. `fix-uro.ps1 -Bisect` (elevated) — with the mask at 0, enable each
   suspect NDIS filter in turn and let the probe say which ones actually
   stop coalescing on your machine.
5. `fix-uro.ps1 -Restore` (elevated) — put everything back.

### What must be off (measured here, 2026-08-05)

* **Hyper-V, Virtual Machine Platform and WSL, plus a reboot.** This is
  the mask-48 cause and it is mandatory. Stopping `winnat` and disabling
  the Hyper-V vnics at runtime does *not* work: the IPSNPI clients
  register with tcpip at boot and the mask stays 48 (tested). After
  uninstalling the features and restarting, the mask read 0 and the same
  traffic went from 300,000 single-datagram receives to 46,905 receives
  carrying up to 8 datagrams each.
* **NDIS filters**: machine-specific, so bisect rather than guess.
  Npcap is the documented offender ([nmap/npcap#737](https://github.com/nmap/npcap/issues/737)).

### Two traps

* `Get-NetAdapterUro` answers a **different** question (hardware
  offload). This rig coalesces happily while that cmdlet reports
  nothing, so an empty result proves nothing about software URO.
* **Loopback can never test URO.** Delivery there is synchronous per
  send, so no batch exists to merge: measured zero coalescing on
  loopback even with the mask at 0. Always probe over a real link
  (`UroProbe wire ...`, which is what `-Bisect` drives).
