#!/usr/bin/env bash
# Rate ladder against a Linux forwarder rung running inside the WSL2 podman
# VM (mirrored networking, so eth0 = the workstation's real LAN address).
# Driven from Git Bash on the workstation. The NAS is generator+sink.
#
# The forwarder binary must already be at /tmp/fwd inside the VM. Deploy it:
#   dotnet publish src/Forwarder.Rung3.LinuxBatched -c Release -r linux-x64 \
#     --self-contained -p:PublishSingleFile=true -o publish/linux
#   wsl -d podman-machine-default -u root -- bash -lc \
#     'cp /mnt/c/Users/Simon/RiderProjects/udp-performance-ladder/publish/linux/Forwarder.Rung3.LinuxBatched /tmp/fwd && chmod +x /tmp/fwd'
#
# Gotchas baked in:
#  - pkill pattern is anchored to '^/tmp/fwd' so it does not kill the
#    launching shell (whose command line also contains the string).
#  - wsl.exe emits "your NNNNN x1 screen size is bogus" warnings to stderr;
#    filter to numeric/pps lines only.
#  - AF_PACKET needs NET_RAW; run the forwarder as -u root. On a real link it
#    self-configures src MAC/IP from AFPACKET_IFACE and resolves the peer MAC
#    via ARP, so only AFPACKET_IFACE=eth0 is needed.
#  - Requires a Hyper-V firewall inbound rule for UDP 5000 (separate from the
#    normal Windows Firewall; default-block inbound under mirrored mode).
#
# Usage: bench/measure-linux-vm.sh <engine> <rate> [rate...]
#   engine: mmsg | gso | afpacket | uring   (uring needs seccomp allowance)
set -uo pipefail

VM="${VM:-podman-machine-default}"
API="${UDPBENCH_API:-http://simondatastore:5390}"
WS_IP="${WS_IP:-192.168.178.143}"
NAS_IP="${NAS_IP:-192.168.178.41}"
DURATION="${DURATION:-10}"
THREADS="${THREADS:-4}"

ENGINE="$1"; shift

vnum() { wsl.exe -d "$VM" -u root -- bash -lc "$1" 2>/dev/null | tr -d '\r' | grep -oE '^[0-9]+$' | tail -1; }

echo "=== Linux $ENGINE (sender threads $THREADS, ${DURATION}s runs) ==="
for RATE in "$@"; do
  wsl.exe -d "$VM" -u root -- bash -lc \
    "pkill -f '^/tmp/fwd' 2>/dev/null; sleep 1; AFPACKET_IFACE=eth0 nohup /tmp/fwd --engine $ENGINE --listen 5000 --to $NAS_IP:6000 --stats 1 > /tmp/f.log 2>&1 & sleep 3" \
    >/dev/null 2>&1
  RX0=$(vnum 'grep -oE "total rx [0-9,]+" /tmp/f.log | tail -1 | tr -d "a-z ,"')
  ID=$(curl -s -X POST "$API/runs" -H 'Content-Type: application/json' \
    -d "{\"target\":\"$WS_IP:5000\",\"size\":32,\"rate\":$RATE,\"sendDurationSeconds\":$DURATION,\"threads\":$THREADS,\"sinkPort\":6000,\"sinkThreads\":4}" \
    | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')
  sleep $((DURATION + 9))
  RX1=$(vnum 'grep -oE "total rx [0-9,]+" /tmp/f.log | tail -1 | tr -d "a-z ,"')
  # forwarder self-reports CPU; take a loaded-interval line
  CPU=$(wsl.exe -d "$VM" -u root -- bash -lc 'grep -oE "cpu +[0-9.]+%" /tmp/f.log | tail -3 | grep -oE "[0-9.]+" | sort -n | tail -1' 2>/dev/null | tr -d '\r' | grep -oE '^[0-9.]+$' | tail -1)
  curl -s "$API/runs/$ID" | RATE=$RATE RX=$(( ${RX1:-0} - ${RX0:-0} )) CPU="${CPU:-?}" python -c "
import json, os, sys
r = json.load(sys.stdin); s = r.get('send')
rate = int(os.environ['RATE']); rx = int(os.environ['RX'])
sent = s['packetsSent'] if s else 0
loss = 100*(sent-rx)/sent if sent else 0
print(f\"{rate:>9,}  fwd_rx {rx:>9,} ({loss:>6.2f}% rx loss)  cpu {os.environ['CPU']}%\")
"
done
wsl.exe -d "$VM" -u root -- bash -lc "pkill -f '^/tmp/fwd'" >/dev/null 2>&1
