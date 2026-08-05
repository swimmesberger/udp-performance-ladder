#!/usr/bin/env bash
# The complete Windows ladder under ONE frozen configuration, so every table
# in the article compares to every other. Config: Defender real-time
# protection + firewall OFF, Realtek driver 10.80.20.407, NIC hygiene per
# ENVIRONMENT.md, CPU = median of loaded stats-line samples.
# (rio/rio-defer/uso ladders from matrix-rung3.sh, same day+config, complete
# this set; rio re-runs here with a 400k row for the intake ceiling.)
set -uo pipefail
cd /c/Users/Simon/RiderProjects/udp-performance-ladder

R1=src/Forwarder.Rung1.Naive/bin/Release/net10.0/Forwarder.Rung1.Naive.exe
R2=src/Forwarder.Rung2.Frugal/bin/Release/net10.0/Forwarder.Rung2.Frugal.exe
RUST=rust/target/release/forwarder-rung4.exe
RUST_ASYNC=rust/target/release/forwarder-rung4-async.exe
RUST_RIO=rust/target/release/forwarder-rung4-rio.exe
R3=src/Forwarder.Rung3.Batched/bin/Release/net10.0/Forwarder.Rung3.Batched.exe

THREADS=8 bench/measure-windows.sh "$R1" "rung1 naive" 150000 200000 250000 300000
THREADS=8 bench/measure-windows.sh "$R2" "rung2 frugal async" 150000 200000 250000 300000
THREADS=8 FWD_ARGS='--sync' bench/measure-windows.sh "$R2" "rung2 frugal sync" 150000 200000 250000 300000
THREADS=8 bench/measure-windows.sh "$RUST" "rust std blocking" 150000 200000 250000 300000
THREADS=8 bench/measure-windows.sh "$RUST_ASYNC" "rust tokio" 150000 200000 250000 300000
THREADS=8 bench/measure-windows.sh "$RUST_RIO" "rust rio" 150000 200000 250000 300000 350000
THREADS=8 FWD_ARGS='--engine rio' bench/measure-windows.sh "$R3" "rio 400k ceiling row" 400000
