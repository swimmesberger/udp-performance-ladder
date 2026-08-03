//! Rung 4: the frugal loop (rung 2) ported to Rust, architecture kept
//! identical on purpose: one thread, blocking recv_from, then one send_to
//! per destination, one reused buffer, destinations resolved once, and the
//! same aligned 1 MB receive buffer. The measured delta against rung 2
//! isolates the runtime, not a redesign.

use std::net::{SocketAddr, ToSocketAddrs, UdpSocket};
use std::process::exit;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::thread;
use std::time::{Duration, Instant};

struct Stats {
    rx_packets: AtomicU64,
    rx_bytes: AtomicU64,
    tx_packets: AtomicU64,
    tx_bytes: AtomicU64,
}

fn main() {
    let mut listen_port: u16 = 5000;
    let mut destinations: Vec<SocketAddr> = Vec::new();
    let mut stats_interval = Duration::from_secs(1);

    let args: Vec<String> = std::env::args().skip(1).collect();
    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "--listen" => {
                i += 1;
                listen_port = args[i].parse().expect("--listen expects a port");
            }
            "--to" => {
                i += 1;
                let resolved = args[i]
                    .to_socket_addrs()
                    .expect("--to expects host:port")
                    .find(SocketAddr::is_ipv4)
                    .expect("no IPv4 address for destination");
                destinations.push(resolved);
            }
            "--stats" => {
                i += 1;
                let secs: f64 = args[i].parse().expect("--stats expects seconds");
                stats_interval = Duration::from_secs_f64(secs);
            }
            other => {
                eprintln!("unknown argument '{other}'; usage: --listen <port> --to <host:port> [--to ...] [--stats <seconds>]");
                exit(1);
            }
        }
        i += 1;
    }
    if destinations.is_empty() {
        eprintln!("at least one --to destination is required");
        exit(1);
    }

    let socket = {
        use socket2::{Domain, Protocol, Socket, Type};
        let s = Socket::new(Domain::IPV4, Type::DGRAM, Some(Protocol::UDP))
            .expect("socket creation failed");
        // Aligned across every rung; the OS default (~64 KB on Windows)
        // overflows on line-rate bursts. See rung 1's confession.
        s.set_recv_buffer_size(1 << 20).expect("SO_RCVBUF failed");
        let addr: SocketAddr = format!("0.0.0.0:{listen_port}").parse().unwrap();
        s.bind(&addr.into()).expect("bind failed");
        UdpSocket::from(s)
    };

    let stats = Arc::new(Stats {
        rx_packets: AtomicU64::new(0),
        rx_bytes: AtomicU64::new(0),
        tx_packets: AtomicU64::new(0),
        tx_bytes: AtomicU64::new(0),
    });

    println!(
        "rung 4 (rust, std sockets): listening on :{listen_port}, forwarding to {} destination(s)",
        destinations.len()
    );

    {
        let stats = Arc::clone(&stats);
        thread::spawn(move || reporter(stats, stats_interval));
    }

    let mut buffer = [0u8; 65536];
    loop {
        let (received, _source) = match socket.recv_from(&mut buffer) {
            Ok(r) => r,
            Err(e) => {
                eprintln!("recv_from failed: {e}");
                exit(1);
            }
        };
        stats.rx_packets.fetch_add(1, Ordering::Relaxed);
        stats.rx_bytes.fetch_add(received as u64, Ordering::Relaxed);

        for destination in &destinations {
            match socket.send_to(&buffer[..received], destination) {
                Ok(_) => {
                    stats.tx_packets.fetch_add(1, Ordering::Relaxed);
                    stats.tx_bytes.fetch_add(received as u64, Ordering::Relaxed);
                }
                Err(_) => {
                    // Same behavior as the C# rungs: a failed send is not fatal.
                }
            }
        }
    }
}

/// Prints the same line shape as the C# rungs so the measurement scripts
/// parse all rungs identically. Rust has no GC, so the alloc/gen0 fields
/// the C# reporter prints are simply absent here.
fn reporter(stats: Arc<Stats>, interval: Duration) {
    let mut previous_rx = 0u64;
    let mut previous_rx_bytes = 0u64;
    let mut previous_tx = 0u64;
    let mut previous_tx_bytes = 0u64;
    let mut last = Instant::now();
    loop {
        thread::sleep(interval);
        let seconds = last.elapsed().as_secs_f64();
        last = Instant::now();

        let rx = stats.rx_packets.load(Ordering::Relaxed);
        let rx_bytes = stats.rx_bytes.load(Ordering::Relaxed);
        let tx = stats.tx_packets.load(Ordering::Relaxed);
        let tx_bytes = stats.tx_bytes.load(Ordering::Relaxed);

        let rx_pps = (rx - previous_rx) as f64 / seconds;
        let rx_mbit = (rx_bytes - previous_rx_bytes) as f64 * 8.0 / seconds / 1_000_000.0;
        let tx_pps = (tx - previous_tx) as f64 / seconds;
        let tx_mbit = (tx_bytes - previous_tx_bytes) as f64 * 8.0 / seconds / 1_000_000.0;

        println!(
            "rx {:>11} pps {:>8.1} Mbit/s | tx {:>11} pps {:>8.1} Mbit/s | total rx {} tx {} drop 0",
            commas(rx_pps as u64),
            rx_mbit,
            commas(tx_pps as u64),
            tx_mbit,
            commas(rx),
            commas(tx),
        );

        previous_rx = rx;
        previous_rx_bytes = rx_bytes;
        previous_tx = tx;
        previous_tx_bytes = tx_bytes;
    }
}

fn commas(n: u64) -> String {
    let digits = n.to_string();
    let mut out = String::with_capacity(digits.len() + digits.len() / 3);
    for (index, ch) in digits.chars().enumerate() {
        if index > 0 && (digits.len() - index) % 3 == 0 {
            out.push(',');
        }
        out.push(ch);
    }
    out
}
