#!/usr/bin/env bash
# Runs INSIDE the VM: start an engine, trigger a load run against it,
# census its threads mid-run, report, clean up. One session, no
# cross-invocation process visibility issues.
# Usage (from Windows): wsl -d podman-machine-default -u root -- bash /mnt/c/Users/Simon/RiderProjects/udp-performance-ladder/bench/census-in-vm.sh <engine> <rate>
set -u
ENGINE="$1"; RATE="$2"
pkill -f '/tmp/fwd' 2>/dev/null; sleep 1
/tmp/fwd --engine "$ENGINE" --listen 5000 --to 192.168.178.41:6000 --stats 1 > /tmp/f.log 2>&1 &
FWD=$!
sleep 2
curl -s -X POST http://192.168.178.41:5390/runs -H 'Content-Type: application/json' \
  -d "{\"target\":\"192.168.178.143:5000\",\"size\":32,\"rate\":$RATE,\"sendDurationSeconds\":12,\"threads\":4,\"sinkPort\":6000,\"sinkThreads\":4}" \
  > /dev/null
sleep 7
echo "=== $ENGINE at $RATE, mid-run ==="
echo "threads: $(ls /proc/$FWD/task | wc -l)"
cat /proc/$FWD/task/*/comm | sort | uniq -c | sort -rn | head -8
echo "cpu line: $(grep -oE 'cpu +[0-9.]+%' /tmp/f.log | tail -1)"
sleep 8
kill $FWD 2>/dev/null
