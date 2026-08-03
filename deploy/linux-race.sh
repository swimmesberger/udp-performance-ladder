#!/usr/bin/env bash
# Three-way Linux race over loopback. Per engine and rate: 3 s discarded
# warmup, 10 s measured, loss from sender count vs the forwarder's own
# counters (baselined after warmup), CPU from /proc/<pid>/stat.
set -uo pipefail

RATES="${RATES:-100000 200000 400000 600000 0}"  # 0 = unthrottled ceiling probe
DUR="${DUR:-10}"
SEND_THREADS="${SEND_THREADS:-4}"
TICKS=$(getconf CLK_TCK)

cpu_ticks() { awk '{print $14+$15}' "/proc/$1/stat" 2>/dev/null || echo 0; }
fwd_totals() { grep -oE 'total rx [0-9,]+ tx [0-9,]+' /tmp/fwd.log | tail -1 | tr -d ','; }

run_engine() {
  local label="$1"; shift
  echo "=== $label ==="
  for RATE in $RATES; do
    "$@" --listen 5000 --to 127.0.0.1:6000 --stats 1 > /tmp/fwd.log 2>&1 &
    FWD=$!
    dotnet /app/bench/UdpBench.dll sink --listen 6000 --duration $((DUR + 22)) --threads 2 > /tmp/sink.log 2>&1 &
    SINK=$!
    sleep 1

    dotnet /app/bench/UdpBench.dll send --target 127.0.0.1:5000 --size 32 --rate $RATE --duration 3 --threads $SEND_THREADS > /dev/null 2>&1
    sleep 1
    BASE=$(fwd_totals); RX0=$(echo "$BASE" | awk '{print $3}'); TX0=$(echo "$BASE" | awk '{print $5}')
    C0=$(cpu_ticks $FWD)

    SENT=$(dotnet /app/bench/UdpBench.dll send --target 127.0.0.1:5000 --size 32 --rate $RATE --duration $DUR --threads $SEND_THREADS 2>/dev/null | grep -oE 'done: [0-9,]+' | tr -d 'done: ,')
    C1=$(cpu_ticks $FWD)
    sleep 1
    TOT=$(fwd_totals); RX1=$(echo "$TOT" | awk '{print $3}'); TX1=$(echo "$TOT" | awk '{print $5}')

    kill $FWD $SINK 2>/dev/null; wait $FWD $SINK 2>/dev/null

    RX=$((RX1 - RX0)); TX=$((TX1 - TX0))
    SINKED=$(grep -oE 'total [0-9,]+' /tmp/sink.log | tail -1 | tr -d 'total ,')
    CPU=$(( (C1 - C0) * 100 / (TICKS * DUR) ))
    LOSS=$(awk -v s="$SENT" -v r="$RX" 'BEGIN { printf("%.2f", (s > 0) ? (100 * (s - r) / s) : 0) }')
    printf '%9s offered  sent %9s  fwd_rx %9s (%5s%% rx loss)  fwd_tx %9s  sink %9s  cpu %3s%%\n' \
      "$RATE" "$SENT" "$RX" "$LOSS" "$TX" "${SINKED:-0}" "$CPU"
  done
}

# On Linux the async path IS .NET's native engine (epoll); sync calls are
# emulated over it with a poll per op, so sync is a lane, not the baseline.
run_engine "plain loop (rung 2, async/epoll)" dotnet /app/rung2/Forwarder.Rung2.Frugal.dll
run_engine "plain loop (rung 2 --sync, emulated)" dotnet /app/rung2/Forwarder.Rung2.Frugal.dll --sync
run_engine "mmsg batching"              dotnet /app/rung3/Forwarder.Rung3.LinuxBatched.dll --engine mmsg
run_engine "io_uring"                   dotnet /app/rung3/Forwarder.Rung3.LinuxBatched.dll --engine uring
run_engine "mmsg + UDP GSO"             dotnet /app/rung3/Forwarder.Rung3.LinuxBatched.dll --engine gso
run_engine "AF_PACKET mmap rings"       dotnet /app/rung3/Forwarder.Rung3.LinuxBatched.dll --engine afpacket
