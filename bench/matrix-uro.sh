#!/usr/bin/env bash
# The Windows symmetric twin of the Linux gso / gso-gro pair: send-side
# stack batching alone (USO), then the same engine with the receive twin
# opted in (URO). On hardware without URO the second lane must measure
# identically, since the option is then inert.
set -uo pipefail
cd /c/Users/Simon/RiderProjects/udp-performance-ladder
EXE=src/Forwarder.Rung3.Batched/bin/Release/net10.0/Forwarder.Rung3.Batched.exe

THREADS=8 FWD_ARGS='--engine uso' \
  bench/measure-windows.sh "$EXE" "uso (send offload only)" 200000 300000
THREADS=8 FWD_ARGS='--engine uso --uro-segment 32' \
  bench/measure-windows.sh "$EXE" "uso + uro opt-in" 200000 300000
