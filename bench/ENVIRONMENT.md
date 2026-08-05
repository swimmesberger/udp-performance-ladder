# Benchmark environment and how to reproduce

Everything needed to rerun the UDP performance ladder measurements from a
fresh context. Written 2026-08-03. The detailed per-run results and the
findings live in `../results/2026-08-03-rung3-and-harness-v2.md` and
`../results/2026-08-03-rung1-rung2.md`; this file is the operational state.

## Hardware and topology

- **Workstation (device under test for Windows rungs)**: AMD Ryzen 7
  9800X3D (8c/16t), 62 GB RAM, Windows 11 build 26200, .NET 10.0.302.
  NIC: Realtek PCIe 2.5GbE Family Controller, driver r8169-class on the
  Windows side, linked at 1 Gbps. LAN IP **192.168.178.143** (interface
  "Ethernet 9").
- **NAS (traffic generator + sink)**: `simondatastore`, LAN IP
  **192.168.178.41**, MAC 00:11:32:EA:20:7D. NICs eth0/eth1 both driver
  **r8168** (Realtek out-of-tree; NO XDP support). Runs the udpbench
  control API. Also hosts the private Docker registry `simondatastore:50000`
  and Portainer.
- **Switch**: 1 GbE, both machines on the same segment (192.168.178.0/24,
  gateway .1, a FritzBox).

## The udpbench control API (on the NAS)

Long-running `udpbench serve` container, deployed via
`deploy/docker-compose.yml`, reachable at **http://simondatastore:5390**.
Drives generator+sink as one HTTP request. **No auth token is set**
(UDPBENCH_API_TOKEN empty) — anyone on the LAN can drive it; set a token
in `deploy/.env` if that matters.

- `POST /runs` body: `{target, size, rate, sendDurationSeconds, threads,
  sink, sinkPort, sinkDurationSeconds, sinkThreads}` → 202 + `{id}`
  (409 if a run is active; one run at a time).
- `GET /runs/{id}` → `{send:{packetsSent,pps,...}, sink:{packets,...},
  loss:{sent,received,lost,percent}}`. **Read loss from `loss`** (compares
  both counts); the sink's own span-based `lossPercent` over-reports with
  multiple threads.
- `GET /healthz` (no token). Default port 5390 (5080 was taken on the NAS).
- Redeploy after a CI image push: `cd deploy && docker compose pull &&
  docker compose up -d`.
- CI (self-hosted runner on the NAS) pushes on every main build:
  `simondatastore:50000/udpbench:latest` and
  `simondatastore:50000/udpladder-forwarder:latest`.

## Generator/sink facts (learned the hard way)

- **One sender thread cannot saturate the link.** Generator plateaus ~540k
  pps unthrottled with the old per-packet path; the batched sendmmsg path
  (Linux container) does >1.3M pps. Use `threads`: 4 for throttled ladders,
  8+ for ceiling probes. For a throttled rate use FEW threads (the
  sleep-based pacer over-sleeps with many; 16 threads delivered 56k of a
  100k target, 4 threads hit it exactly).
- **The sink ceilings ~220k pps over the wire on the NAS** even multi-
  threaded. Above that, "sink received" measures the sink, not the
  forwarder. So the primary metric is sender-count vs the forwarder's OWN
  rx counter. Sink numbers validate full delivery only below ~220k.
- The batched generator emits **line-rate bursts of up to 64** per thread
  wakeup, not a smooth stream. This is a benchmark variable: it makes
  small-socket-buffer rungs lose far more. All published numbers use it.

## NIC hygiene applied to the workstation (for clean numbers)

Set via `Set-NetAdapterAdvancedProperty -Name 'Ethernet 9' -RegistryKeyword
<kw> -RegistryValue <v>` (needs an ELEVATED shell; non-admin gets Access
is denied). All must be off/set or numbers are polluted:
- `*FlowControl` = 0 (else 802.3x PAUSE lets the NAS silently throttle tx)
- `*EEE` = 0, `EnableGreenEthernet` = 0, `PowerSavingMode` = 0, `GigaLite` = 0
- `*ReceiveBuffers` = 4096 (driver max; absorbs the 64-packet bursts)
- `*InterruptModeration` left ON (throughput-realistic default)

**A driver update silently resets part of this list.** The 2026-08-05
update (Realtek 1125.26.50.2025 -> 10.80.20.407) reset `*ReceiveBuffers`
to 1024 and re-enabled `EnableGreenEthernet` and `GigaLite` while
preserving the rest. Re-run the audit after ANY driver change:
`Get-NetAdapterAdvancedProperty -Name 'Ethernet 9' | ? { $_.RegistryKeyword
-in '*ReceiveBuffers','EnableGreenEthernet','GigaLite','*FlowControl','*EEE' }`.
The 10.80 driver line also shifts engine numbers in opposite directions
(per-packet RIO ~10 points worse, USO ~9 points better at 200k), so
matrices from different driver versions are not comparable; the results
docs record the driver version per run set. URO remains unadvertised on
10.80.20.407 (`Get-NetAdapterUro` empty).

Windows Firewall: the forwarder .exe rules exist for Public AND Private
profiles (Ethernet 9 is a Private network). A new/unsigned forwarder binary
triggers a Defender prompt on first run; until answered it shows 100% loss
at 0% CPU.

## Running the Windows rungs

Build: `dotnet build -c Release` at the repo root. Then:
```
THREADS=8 bench/measure-windows.sh \
  src/Forwarder.Rung3.Batched/bin/Release/net10.0/Forwarder.Rung3.Batched.exe \
  "label" 200000 300000
```
Forwarders self-report CPU in their stats line ("cpu NN.N%"), which is the
authoritative CPU source now (external Get-Process sampling was flaky).
Rung 2 has a `--sync` flag (pass via `FWD_ARGS=--sync`) — the blocking
control for the Rust comparison.

## Running the Linux rungs (WSL2 podman VM)

**Mirrored networking is required** and already configured in
`C:\Users\Simon\.wslconfig` (`networkingMode=mirrored`). This gives the VM
the workstation's real LAN IP on eth0 (L2 adjacency, needed for AF_PACKET).

Side effects of mirrored mode, and how to revert:
- Podman's Windows client CANNOT reach its VM (SSH port-forward breaks).
  Work AROUND it: run commands inside the VM with
  `wsl -d podman-machine-default -u root -- bash -lc '...'`. Do NOT rely on
  the `podman` CLI from Windows.
- `wsl --shutdown` also stops Docker Desktop's distro.
- **A Hyper-V firewall rule is required** (separate from Windows Firewall,
  default-block inbound under mirrored mode):
  `New-NetFirewallHyperVRule -Name "udp-ladder-5000" -DisplayName "..."
   -Direction Inbound -VMCreatorId "{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}"
   -Protocol UDP -LocalPorts 5000 -Action Allow`
- **Revert everything**: delete `.wslconfig`, `wsl --shutdown`. Podman's
  Windows client works again after that.

Deploy + measure:
```
dotnet publish src/Forwarder.Rung3.LinuxBatched -c Release -r linux-x64 \
  --self-contained -p:PublishSingleFile=true -o publish/linux
wsl -d podman-machine-default -u root -- bash -lc \
  'cp /mnt/c/Users/Simon/RiderProjects/udp-performance-ladder/publish/linux/Forwarder.Rung3.LinuxBatched /tmp/fwd && chmod +x /tmp/fwd'
bench/measure-linux-vm.sh gso 200000 300000
```
Gotchas: pkill must be anchored `^/tmp/fwd`; wsl.exe prints "screen size is
bogus" to stderr (filter it); `Text file busy` on cp means a forwarder is
still running (pkill first).

**These Linux numbers come from a virtualized adapter** (Realtek NIC →
Windows NDIS → Hyper-V vSwitch → netvsc → VM). They are comparable to each
other but NOT to the Windows bare-metal rungs (see the cross-OS caveat in
the results doc: process CPU excludes softirq work, and virtualization adds
cost — the biases go opposite ways).

## The container race (self-contained, no LAN needed)

`deploy/Dockerfile.linux-race` + `deploy/linux-race.sh` run all Linux
engines over the container's loopback:
```
podman build --network host -f deploy/Dockerfile.linux-race -t linux-race .
podman run --rm --network host --security-opt seccomp=unconfined \
  --cap-add=NET_RAW linux-race
```
`--network host` works around a netavark/nftables error; `seccomp=unconfined`
is REQUIRED for the io_uring engine (default seccomp blocks io_uring_setup
entirely); `--cap-add=NET_RAW` for AF_PACKET. Loopback has no NIC, so GSO
reaches ~930k pps/core there — not comparable to the LAN.

## XDP / rung 5 verdict

No XDP-capable NIC exists in this environment: Realtek on Windows has no
NDIS XDP extensions (generic mode only, unpublishable); r8168/r8169 on
Linux have no XDP hook; the WSL netvsc adapter implements XDP but sits
behind all the physical/virtual layers, so it measures XDP-vs-sockets
inside the VM, not XDP's real claim. Rung 5 is therefore written as a
DOCUMENTED LIMITATION. To actually measure it: an Intel NIC (igc/igb/i350/
X520) ~35 EUR, or two cloud instances (AWS ENA / GCP gVNIC support native
XDP). AF_PACKET carries the raw-frame slot in rung 5 since it needs no
driver support.
