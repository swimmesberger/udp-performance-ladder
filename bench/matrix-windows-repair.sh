#!/usr/bin/env bash
# Repair pass for matrix-windows-all.sh: the .NET ladders died of
# SIO_UDP_CONNRESET (now disabled) and the Rust binaries lacked a cpu
# stats field (now added). Same frozen config as the rest of the set.
set -uo pipefail
cd /c/Users/Simon/RiderProjects/udp-performance-ladder

R1=src/Forwarder.Rung1.Naive/bin/Release/net10.0/Forwarder.Rung1.Naive.exe
R2=src/Forwarder.Rung2.Frugal/bin/Release/net10.0/Forwarder.Rung2.Frugal.exe
RUST=rust/target/release/forwarder-rung4.exe
RUST_ASYNC=rust/target/release/forwarder-rung4-async.exe
RUST_RIO=rust/target/release/forwarder-rung4-rio.exe

THREADS=8 bench/measure-windows.sh "$R1" "rung1 naive" 150000 200000 250000 300000
THREADS=8 bench/measure-windows.sh "$R2" "rung2 frugal async" 150000 200000 250000 300000
THREADS=8 FWD_ARGS='--sync' bench/measure-windows.sh "$R2" "rung2 frugal sync" 150000 200000 250000 300000
THREADS=8 bench/measure-windows.sh "$RUST" "rust std blocking" 150000 200000 250000 300000
THREADS=8 bench/measure-windows.sh "$RUST_ASYNC" "rust tokio" 150000 200000 250000 300000
THREADS=8 bench/measure-windows.sh "$RUST_RIO" "rust rio" 150000 200000 250000 300000 350000
