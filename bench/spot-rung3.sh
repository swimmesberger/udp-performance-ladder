#!/usr/bin/env bash
# Quick spot-check of two rung 3 engines at 200k, e.g. after a driver change.
set -uo pipefail
cd /c/Users/Simon/RiderProjects/udp-performance-ladder
EXE=src/Forwarder.Rung3.Batched/bin/Release/net10.0/Forwarder.Rung3.Batched.exe
THREADS=8 FWD_ARGS='--engine rio' bench/measure-windows.sh "$EXE" "spot rio" 200000
THREADS=8 FWD_ARGS='--engine uso' bench/measure-windows.sh "$EXE" "spot uso" 200000
