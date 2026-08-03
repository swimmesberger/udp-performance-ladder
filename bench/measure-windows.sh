#!/usr/bin/env bash
# Rate ladder against a Windows-native forwarder rung, driven from Git Bash
# on the workstation. The NAS runs the generator+sink via the udpbench
# control API; the forwarder runs locally as a Windows .exe.
#
# Primary cliff metric: sender count vs the forwarder's OWN rx counter,
# baselined after a discarded warmup. The sink over the wire ceilings near
# ~220k pps on the NAS, so sink-delivered is only authoritative below that.
#
# Forwarders self-report CPU in their stats line now (Process CPU per
# interval); this script's external cpu_of() is a fallback and is less
# reliable. Prefer reading the "cpu NN.N%" field from the forwarder's own
# fwd.log for CPU.
#
# Usage: [THREADS=n] [FWD_ARGS=--sync] measure-windows.sh <exe-rel-path> <label> <rate> [rate...]
# Example:
#   THREADS=8 bench/measure-windows.sh \
#     src/Forwarder.Rung3.Batched/bin/Release/net10.0/Forwarder.Rung3.Batched.exe \
#     "Rung 3 RIO" 150000 200000 250000 300000
set -uo pipefail

R="${LADDER_ROOT:-C:/Users/Simon/RiderProjects/udp-performance-ladder}"
API="${UDPBENCH_API:-http://simondatastore:5390}"
WS_IP="${WS_IP:-192.168.178.143}"   # workstation LAN IP (forwarder listens here)
NAS_IP="${NAS_IP:-192.168.178.41}"  # NAS LAN IP (sink runs here, port 6000)
DURATION="${DURATION:-10}"
THREADS="${THREADS:-4}"             # 4 for throttled ladders; 8+ for ceiling probes

EXE="$1"; LABEL="$2"; shift 2

fwd_totals() {
  grep -oE 'total rx [0-9,]+ tx [0-9,]+' "$R/fwd.log" | tail -1 | tr -d ','
}

echo "=== $LABEL (sender threads $THREADS, ${DURATION}s runs) ==="

for RATE in "$@"; do
  "$R/$EXE" --listen 5000 --to "$NAS_IP":6000 --stats 1 ${FWD_ARGS:-} > "$R/fwd.log" 2>&1 &
  FWD=$!
  sleep 2

  # Warmup: a fresh forwarder pays JIT on its first packets. Discarded, and
  # the forwarder counters are baselined afterwards so warmup loss cannot
  # contaminate the measured window.
  curl -s -X POST "$API/runs" -H 'Content-Type: application/json' \
    -d "{\"target\":\"$WS_IP:5000\",\"size\":32,\"rate\":$RATE,\"sendDurationSeconds\":3,\"threads\":$THREADS,\"sinkPort\":6000,\"sinkThreads\":4}" \
    > /dev/null
  sleep 11
  BASE=$(fwd_totals); RX0=$(echo "$BASE" | awk '{print $3}'); TX0=$(echo "$BASE" | awk '{print $5}')

  ID=$(curl -s -X POST "$API/runs" -H 'Content-Type: application/json' \
    -d "{\"target\":\"$WS_IP:5000\",\"size\":32,\"rate\":$RATE,\"sendDurationSeconds\":$DURATION,\"threads\":$THREADS,\"sinkPort\":6000,\"sinkThreads\":4}" \
    | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')

  sleep $((DURATION + 8))
  RESULT=$(curl -s "$API/runs/$ID")
  # Read the forwarder's own CPU (median-ish: last loaded interval line).
  CPU=$(grep -oE 'cpu +[0-9.]+%' "$R/fwd.log" | tail -3 | grep -oE '[0-9.]+' | sort -n | tail -1)
  kill "$FWD" 2>/dev/null
  sleep 1

  TOTALS=$(fwd_totals)
  FWD_RX=$(( $(echo "$TOTALS" | awk '{print $3}') - ${RX0:-0} ))

  echo "$RESULT" | RATE=$RATE FWDRX="${FWD_RX:-0}" CPU="${CPU:-0}" python -c "
import json, os, sys
r = json.load(sys.stdin); s = r.get('send'); l = r.get('loss')
rate = int(os.environ['RATE']); fwdrx = int(os.environ['FWDRX'] or 0)
if not s: print(f'{rate:>9,}  run incomplete'); sys.exit()
sent = s['packetsSent']
print(f\"{rate:>9,}  sent {sent:>9,}  fwd_rx {fwdrx:>9,} ({100*(sent-fwdrx)/sent:>5.2f}% rx loss)  cpu {os.environ['CPU']}%\")
"
done
rm -f "$R/fwd.log"
