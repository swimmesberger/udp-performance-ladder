#!/usr/bin/env bash
# Interleaved A/B for the USO-vs-USO+URO pair: three repeats each,
# alternating, so machine-state drift lands on both sides equally.
set -uo pipefail
cd /c/Users/Simon/RiderProjects/udp-performance-ladder
EXE=src/Forwarder.Rung3.Batched/bin/Release/net10.0/Forwarder.Rung3.Batched.exe

for round in 1 2 3; do
  THREADS=8 FWD_ARGS='--engine uso' \
    bench/measure-windows.sh "$EXE" "round $round: uso" 200000
  THREADS=8 FWD_ARGS='--engine uso --uro-segment 32' \
    bench/measure-windows.sh "$EXE" "round $round: uso+uro" 200000
done
