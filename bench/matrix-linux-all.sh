#!/usr/bin/env bash
# The Linux engine set over the real link, same day and host config as
# matrix-windows-all.sh (Defender off, driver 10.80.20.407). All engines in
# one place: the two plain-socket dispatch models, the three batching
# families, and the raw-frame ring. io_uring's first non-loopback outing.
set -uo pipefail
cd /c/Users/Simon/RiderProjects/udp-performance-ladder

FWD_BIN=/tmp/fwd2 bench/measure-linux-vm.sh plain      200000
FWD_BIN=/tmp/fwd2 bench/measure-linux-vm.sh plain-sync 200000
bench/measure-linux-vm.sh mmsg          200000 300000
bench/measure-linux-vm.sh uring         200000 300000
bench/measure-linux-vm.sh uring-gso     200000 300000
bench/measure-linux-vm.sh uring-gso-gro 200000 300000
bench/measure-linux-vm.sh gso           200000 300000
bench/measure-linux-vm.sh gso-gro       200000 300000
bench/measure-linux-vm.sh afpacket      200000 300000
