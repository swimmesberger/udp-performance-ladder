#!/usr/bin/env bash
# The three .NET socket ladders, after the ENOBUFS/connreset hardening.
set -uo pipefail
cd /c/Users/Simon/RiderProjects/udp-performance-ladder
R1=src/Forwarder.Rung1.Naive/bin/Release/net10.0/Forwarder.Rung1.Naive.exe
R2=src/Forwarder.Rung2.Frugal/bin/Release/net10.0/Forwarder.Rung2.Frugal.exe
THREADS=8 bench/measure-windows.sh "$R1" "rung1 naive" 150000 200000 250000 300000
THREADS=8 bench/measure-windows.sh "$R2" "rung2 frugal async" 150000 200000 250000 300000
THREADS=8 FWD_ARGS='--sync' bench/measure-windows.sh "$R2" "rung2 frugal sync" 150000 200000 250000 300000
