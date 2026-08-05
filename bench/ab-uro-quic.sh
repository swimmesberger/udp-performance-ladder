#!/usr/bin/env bash
# What URO is worth at QUIC-sized payloads: interleaved A/B of USO alone
# against USO + URO, 1200 B datagrams (the size QUIC actually sends), three
# rounds each, alternating.
#
# Rate note: 1200 B payloads put ~1266 B on the wire, so 1 GbE saturates
# near 98,000 pps. 80,000 keeps the link out of the measurement, which is
# also why this project's main ladder uses 32 B: on a gigabit link only
# tiny datagrams can stress a forwarder's CPU before the cable gives up.
set -uo pipefail
cd /c/Users/Simon/RiderProjects/udp-performance-ladder
EXE=src/Forwarder.Rung3.Batched/bin/Release/net10.0/Forwarder.Rung3.Batched.exe

for round in 1 2 3 4 5; do
  DURATION=20 THREADS=4 SIZE=1200 FWD_ARGS='--engine uso' \
    bench/measure-windows.sh "$EXE" "round $round: uso (1200 B)" 80000
  DURATION=20 THREADS=4 SIZE=1200 FWD_ARGS='--engine uso --uro-segment 1200' \
    bench/measure-windows.sh "$EXE" "round $round: uso+uro (1200 B)" 80000
done
