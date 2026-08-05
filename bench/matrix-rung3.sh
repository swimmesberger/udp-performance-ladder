#!/usr/bin/env bash
# The rung 3 Windows engine matrix, run back to back for same-machine-state
# comparability: per-request RIO, deferred/committed RIO, USO packed sends,
# and USO with URO coalescing.
set -uo pipefail
cd /c/Users/Simon/RiderProjects/udp-performance-ladder
EXE=src/Forwarder.Rung3.Batched/bin/Release/net10.0/Forwarder.Rung3.Batched.exe

THREADS=8 FWD_ARGS='--engine rio' \
  bench/measure-windows.sh "$EXE" "A rio baseline" 150000 200000 250000 300000
THREADS=8 FWD_ARGS='--engine rio-defer' \
  bench/measure-windows.sh "$EXE" "B rio-defer" 150000 200000 250000 300000 400000
THREADS=8 FWD_ARGS='--engine uso' \
  bench/measure-windows.sh "$EXE" "C uso" 150000 200000 250000 300000 400000
THREADS=8 FWD_ARGS='--engine uso --uro-segment 32' \
  bench/measure-windows.sh "$EXE" "D uso+uro" 200000 300000 400000
