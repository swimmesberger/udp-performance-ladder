#!/usr/bin/env bash
# One manual run that prints every per-second stats sample, to choose a fair
# CPU statistic (the JIT spike, steady state, and idle tail are all visible).
# Usage: cpu-probe.sh <engine-args...> <rate>
set -uo pipefail
R="/c/Users/Simon/RiderProjects/udp-performance-ladder"
EXE="$R/src/Forwarder.Rung3.Batched/bin/Release/net10.0/Forwarder.Rung3.Batched.exe"
RATE="${@: -1}"
ENGINE_ARGS="${@:1:$#-1}"

"$EXE" --listen 5000 --to 192.168.178.41:6000 --stats 1 $ENGINE_ARGS > /tmp/fwd-probe.log 2>&1 &
FWD=$!
sleep 2
curl -s -X POST http://simondatastore:5390/runs -H 'Content-Type: application/json' \
  -d "{\"target\":\"192.168.178.143:5000\",\"size\":32,\"rate\":$RATE,\"sendDurationSeconds\":10,\"threads\":8,\"sinkPort\":6000,\"sinkThreads\":4}" \
  > /dev/null
sleep 14
kill $FWD 2>/dev/null
grep -E 'rx ' /tmp/fwd-probe.log | awk '{for(i=1;i<=NF;i++){if($i=="rx")printf "rx=%s ",$(i+1);if($i=="cpu")printf "cpu=%s",$(i+1)}printf "\n"}'
