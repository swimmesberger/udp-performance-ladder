#!/usr/bin/env bash
# Count io-wq worker threads inside the forwarder process mid-run, to prove
# (or refute) that a given engine's CPU is worker-pool threads.
# Usage: thread-probe.sh <engine> <rate>
set -uo pipefail
VM="${VM:-podman-machine-default}"
API="${UDPBENCH_API:-http://simondatastore:5390}"
ENGINE="$1"; RATE="$2"

wsl.exe -d "$VM" -u root -- bash -lc \
  "pkill -f '^/tmp/fwd' 2>/dev/null; sleep 1; nohup /tmp/fwd --engine $ENGINE --listen 5000 --to 192.168.178.41:6000 --stats 1 > /tmp/f.log 2>&1 & sleep 2" \
  >/dev/null 2>&1

curl -s -X POST "$API/runs" -H 'Content-Type: application/json' \
  -d "{\"target\":\"192.168.178.143:5000\",\"size\":32,\"rate\":$RATE,\"sendDurationSeconds\":10,\"threads\":4,\"sinkPort\":6000,\"sinkThreads\":4}" \
  > /dev/null
sleep 6

echo "=== $ENGINE at $RATE, mid-run thread census ==="
wsl.exe -d "$VM" -u root -- bash -lc '
  PID=$(pgrep -f "^/tmp/fwd" | head -1)
  echo "pid $PID, total threads: $(ls /proc/$PID/task | wc -l)"
  for t in /proc/$PID/task/*/comm; do cat $t; done | sort | uniq -c | sort -rn
' 2>/dev/null | tr -d '\r'

sleep 8
wsl.exe -d "$VM" -u root -- bash -lc "pkill -f '^/tmp/fwd'" >/dev/null 2>&1
