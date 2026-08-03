# The UDP performance ladder

Benchmark code for the blog post [The UDP performance ladder: from UdpClient to XDP](https://blog.wimmesberger.dev/posts/udp-performance-ladder/).

One UDP port in, N destinations out. The forwarder is implemented repeatedly, each version removing one layer of per-packet cost, and every rung is measured the same way on real hardware. This repository holds the forwarder implementations, the load generator / sink used to measure them, and the BenchmarkDotNet micro-benchmarks.

## The rungs

| Rung | Name             | What changes                                                    | Where                        | Status  |
| ---- | ---------------- | --------------------------------------------------------------- | ---------------------------- | ------- |
| 1    | The naive loop   | `UdpClient`, one await per operation, fresh buffer per packet   | `src/Forwarder.Rung1.Naive`  | done    |
| 2    | The frugal loop  | Raw `Socket`, pinned reused buffer, .NET 8 `SocketAddress` APIs | `src/Forwarder.Rung2.Frugal` | done    |
| 3    | The batched kernel | Registered I/O (Windows) / io_uring (Linux)                   | `src/Forwarder.Rung3.Batched` | Windows done, io_uring planned |
| 4    | The native rewrite | Rungs 2 and 3 in Rust                                         | `rust/forwarder-rung4`       | std done, io_uring/RIO planned |
| 5    | The stack bypass | AF_XDP (Linux) / XDP-for-Windows                                | `xdp/`                       | planned |

## Running a forwarder

Requires the .NET 10 SDK.

```
dotnet run -c Release --project src/Forwarder.Rung1.Naive -- --listen 5000 --to 192.168.1.20:6000 --to 192.168.1.21:6000
```

Both rungs take the same arguments:

- `--listen <port>`: UDP port to listen on (default 5000)
- `--to <host:port>`: destination, repeatable, at least one required
- `--stats <seconds>`: stats print interval (default 1)

The forwarder prints receive/forward rates once per interval.

## Measuring with udpbench

`tools/UdpBench` is the load generator and sink in one binary. Every datagram carries a 64-bit sequence number, so the sink can report loss without talking to the sender.

Start a sink on the destination machine:

```
dotnet run -c Release --project tools/UdpBench -- sink --listen 6000
```

Blast packets at the forwarder from the generator machine:

```
dotnet run -c Release --project tools/UdpBench -- send --target 192.168.1.10:5000 --size 32 --rate 250000 --duration 30
```

- `--rate 0` (default) sends unthrottled: as fast as one thread can.
- `--size` is the UDP payload in bytes, minimum 8 (the sequence number).
- `--duration 0` runs until Ctrl+C.

The sink also takes `--duration <seconds>` to exit on its own and print the
summary, which is what scripted runs (CI, the benchmark harness) use instead
of sending signals.

The sink prints per-second rates and a final summary with received count, the sequence span it observed, and the loss derived from the gap between the two. The loss number assumes a single sender per sink.

### Generator container

The generator is meant to run on a separate machine on the same switch (see the blog post's methodology chapter for why loopback numbers are not publishable). To build the container:

```
docker build -f tools/UdpBench/Dockerfile -t udpbench .
docker run --rm --network host udpbench send --target 192.168.1.10:5000 --size 32 --rate 250000 --duration 30
```

`--network host` matters: a bridge network adds its own forwarding layer and pollutes the numbers.

## The control API

Running a benchmark by redeploying a container per run gets old fast, so `udpbench serve` starts a long-running HTTP control plane. Deploy it once on the generator box and trigger every later run over HTTP:

```
cd deploy
cp .env.example .env    # set UDPBENCH_API_TOKEN
docker compose up -d
```

CI pushes the image to the private registry on every main build, so `docker compose pull && docker compose up -d` picks up new versions.

A run starts the sink in-process, blasts the target, and reports both summaries:

```
curl -X POST http://<generator>:5390/runs \
  -H "Authorization: Bearer $UDPBENCH_API_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"target":"192.168.1.10:5000","size":32,"rate":250000,"sendDurationSeconds":30,"sinkPort":6000}'
```

| Endpoint          | Does                                                          |
| ----------------- | ------------------------------------------------------------- |
| `POST /runs`      | Starts a run, returns 202 with its id (409 if one is running)  |
| `GET /runs/{id}`  | Status plus the send and sink summaries once completed         |
| `GET /runs`       | Every run this process has seen, newest first                  |
| `GET /healthz`    | Liveness; the only endpoint exempt from the bearer token       |

Request fields: `target` (required), `size` (default 32), `rate` (packets per second, 0 = unthrottled), `sendDurationSeconds`, `threads` (sender threads, default 1), `sink` (default true), `sinkPort` (default 6000), `sinkDurationSeconds` (defaults to the send duration plus 5 s so the sink catches the tail).

Read loss from the response's `loss` object, which compares the sender's and sink's counts directly. The sink's own `lossPercent` is derived from sequence gaps alone, which is what a standalone sink has to do, but it over-reports by a fraction of a percent when several sender threads stop at slightly different points.

**One thread cannot saturate a fast link.** A single loop pays one syscall per packet and tops out in the low hundreds of thousands of packets per second on good hardware, far less on a NAS. Raise `threads` until the generator's own rate stops climbing, and confirm it comfortably exceeds the forwarder you are measuring; otherwise you are benchmarking the generator.

Only one run executes at a time, so a stray second request cannot corrupt a measurement in progress. Results live in memory and are lost on restart; copy anything worth keeping into the results table.

Point the forwarder's `--to` at the generator box's `sinkPort` so the fan-out lands in the sink.

## Micro-benchmarks

`benchmarks/MicroBenchmarks` compares the per-datagram cost of the rung 1 and rung 2 receive paths over loopback with BenchmarkDotNet:

```
dotnet run -c Release --project benchmarks/MicroBenchmarks
```

The interesting column is allocations per operation, not time: loopback timing says nothing about wire performance, but the allocation delta between `UdpClient` and the `SocketAddress`-based raw socket path is real and travels with the code.

## Methodology

The measurement setup (topology, metrics, and the ways this kind of benchmark lies) is documented in the blog post's "How every rung is measured" chapter. Published numbers come from a 1 GbE LAN setup with a dedicated generator machine; nothing in this repository fakes a result, and empty result slots stay empty until the harness has run.

## License

[MIT](LICENSE)
