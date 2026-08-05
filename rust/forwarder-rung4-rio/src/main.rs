//! Rung 4 (RIO): the rung 3 engine — Windows Registered I/O — ported to
//! Rust 1:1. Same slot-rotation design and constants as the C# original
//! (src/Forwarder.Rung3.Batched/RioForwarder.cs): one pool whose slots
//! rotate receive -> send -> free, a constant posted-receive count, and an
//! event-driven completion loop where RIODequeueCompletion is a user-mode
//! read and the kernel is only signaled (RIONotify) when the queue runs
//! dry. The CLI and stats reporter are copied from the std-socket rung 4
//! so the measurement scripts parse every rung identically.

#[cfg(not(windows))]
fn main() {
    eprintln!("rung 4 (rust, RIO) is Windows-only; the Linux counterpart is io_uring, not yet implemented");
    std::process::exit(2);
}

#[cfg(windows)]
fn main() {
    win::main();
}

#[cfg(windows)]
mod win {
    use std::net::{SocketAddr, SocketAddrV4, ToSocketAddrs};
    use std::process::exit;
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::sync::Arc;
    use std::thread;
    use std::time::{Duration, Instant};

    use windows_sys::Win32::Networking::WinSock::{
        bind, setsockopt, WSAGetLastError, WSAIoctl, WSASocketW, WSAStartup, AF_INET,
        INVALID_SOCKET, IPPROTO_UDP, RIORESULT, RIO_BUF, RIO_BUFFERID, RIO_CORRUPT_CQ, RIO_CQ,
        RIO_EVENT_COMPLETION, RIO_EXTENSION_FUNCTION_TABLE, RIO_NOTIFICATION_COMPLETION,
        RIO_NOTIFICATION_COMPLETION_0, RIO_NOTIFICATION_COMPLETION_0_0, RIO_RQ, SOCKADDR, SOCKET,
        SOCK_DGRAM, SOL_SOCKET, SO_RCVBUF, SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER, WSADATA,
        WSAID_MULTIPLE_RIO, WSA_FLAG_OVERLAPPED, WSA_FLAG_REGISTERED_IO,
    };
    use windows_sys::Win32::System::Threading::{CreateEventW, WaitForSingleObject};

    const POOL_SLOTS: usize = 12288; // rotating: receive -> send -> free
    const POSTED_RECEIVES: usize = 4096; // held constant by construction
    const SLOT_SIZE: usize = 2048; // fits any non-jumbo datagram
    const ADDR_STRIDE: usize = 32; // SOCKADDR_INET is 28, keep slots aligned
    const DEQUEUE_BATCH: usize = 256;
    const SEND_CONTEXT_BIT: u64 = 1 << 63;
    const SOCKADDR_INET_SIZE: u32 = 28;

    // mswsock.h: #define RIO_INVALID_BUFFERID ((RIO_BUFFERID)0xFFFFFFFF)
    // (windows-sys 0.61 does not export it, so declared by hand.)
    const RIO_INVALID_BUFFERID: RIO_BUFFERID = 0xFFFF_FFFF;

    struct Stats {
        rx_packets: AtomicU64,
        rx_bytes: AtomicU64,
        tx_packets: AtomicU64,
        tx_bytes: AtomicU64,
        dropped: AtomicU64,
    }

    pub fn main() {
        let mut listen_port: u16 = 5000;
        let mut destinations: Vec<SocketAddrV4> = Vec::new();
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
                        .find_map(|a| match a {
                            SocketAddr::V4(v4) => Some(v4),
                            SocketAddr::V6(_) => None,
                        })
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

        let stats = Arc::new(Stats {
            rx_packets: AtomicU64::new(0),
            rx_bytes: AtomicU64::new(0),
            tx_packets: AtomicU64::new(0),
            tx_bytes: AtomicU64::new(0),
            dropped: AtomicU64::new(0),
        });

        println!(
            "rung 4 (rust, RIO): listening on :{listen_port}, forwarding to {} destination(s)",
            destinations.len()
        );

        {
            let stats = Arc::clone(&stats);
            thread::spawn(move || reporter(stats, stats_interval));
        }

        // The engine loops until the process is killed (Ctrl+C); like the C#
        // rung, every handle and the registered buffer live to process exit.
        let mut forwarder = RioForwarder::start(listen_port, &destinations, stats);
        forwarder.run();
    }

    /// The rung 3 engine, field for field. Handles are raw isize/pointer
    /// values on purpose: they all live until process exit, and wrapping
    /// them would add per-packet overhead this path exists to avoid.
    struct RioForwarder {
        receive_ex: unsafe extern "system" fn(
            RIO_RQ,
            *const RIO_BUF,
            u32,
            *const RIO_BUF,
            *const RIO_BUF,
            *const RIO_BUF,
            *const RIO_BUF,
            u32,
            *const core::ffi::c_void,
        ) -> i32,
        send_ex: unsafe extern "system" fn(
            RIO_RQ,
            *const RIO_BUF,
            u32,
            *const RIO_BUF,
            *const RIO_BUF,
            *const RIO_BUF,
            *const RIO_BUF,
            u32,
            *const core::ffi::c_void,
        ) -> i32,
        dequeue_completion: unsafe extern "system" fn(RIO_CQ, *mut RIORESULT, u32) -> u32,
        notify: unsafe extern "system" fn(RIO_CQ) -> i32,

        completion_queue: RIO_CQ,
        request_queue: RIO_RQ,
        buffer_id: RIO_BUFFERID,
        event: windows_sys::Win32::Foundation::HANDLE,
        // Held only to document the registered pool's lifetime (until
        // process exit); RIO addresses it through buffer_id + offsets.
        _memory: *mut u8,

        // One pool; slots rotate roles: posted as a receive, then (holding
        // data) sent from directly, then returned to the free stack. Every
        // receive completion posts exactly one replacement receive, normally
        // a free slot so the just-filled slot can be sent from with zero
        // copies. If the free stack is empty (tx backpressure), the filled
        // slot itself is reposted: that one datagram is dropped on our own
        // counter, and the posted-receive count never shrinks, so RIO can
        // never drop invisibly on an empty ring (no OS counter records
        // those).
        pending_sends: Vec<u32>,
        free_slots: Vec<u32>,

        destination_count: usize,
        stats: Arc<Stats>,
    }

    fn data_offset(slot: usize) -> usize {
        slot * SLOT_SIZE
    }
    fn addr_offset(slot: usize) -> usize {
        POOL_SLOTS * SLOT_SIZE + slot * ADDR_STRIDE
    }
    fn destination_addr_offset(index: usize) -> usize {
        POOL_SLOTS * SLOT_SIZE + POOL_SLOTS * ADDR_STRIDE + index * ADDR_STRIDE
    }

    fn fatal(what: &str) -> ! {
        let error = unsafe { WSAGetLastError() };
        eprintln!("{what} failed: {error}");
        exit(1);
    }

    /// Writes an IPv4 SOCKADDR_INET into 28 zeroed bytes at `destination`.
    unsafe fn write_sockaddr(destination: *mut u8, endpoint: &SocketAddrV4) {
        std::ptr::write_bytes(destination, 0, SOCKADDR_INET_SIZE as usize);
        *destination = 2; // AF_INET, little-endian short
        *destination.add(1) = 0;
        let port = endpoint.port();
        *destination.add(2) = (port >> 8) as u8; // port in network order
        *destination.add(3) = port as u8;
        let octets = endpoint.ip().octets();
        std::ptr::copy_nonoverlapping(octets.as_ptr(), destination.add(4), 4);
    }

    impl RioForwarder {
        fn start(listen_port: u16, destinations: &[SocketAddrV4], stats: Arc<Stats>) -> Self {
            let destination_count = destinations.len();
            unsafe {
                let mut wsa_data: WSADATA = std::mem::zeroed();
                let error = WSAStartup(0x0202, &mut wsa_data);
                if error != 0 {
                    eprintln!("WSAStartup failed: {error}");
                    exit(1);
                }

                let socket: SOCKET = WSASocketW(
                    AF_INET as i32,
                    SOCK_DGRAM,
                    IPPROTO_UDP,
                    std::ptr::null(),
                    0,
                    WSA_FLAG_OVERLAPPED | WSA_FLAG_REGISTERED_IO,
                );
                if socket == INVALID_SOCKET {
                    fatal("WSASocketW");
                }

                // Aligned across every rung; the OS default (~64 KB on
                // Windows) overflows on line-rate bursts.
                let rcvbuf: i32 = 1 << 20;
                if setsockopt(
                    socket,
                    SOL_SOCKET,
                    SO_RCVBUF,
                    &rcvbuf as *const i32 as *const u8,
                    std::mem::size_of::<i32>() as i32,
                ) != 0
                {
                    fatal("setsockopt(SO_RCVBUF)");
                }

                let mut bind_addr = [0u8; SOCKADDR_INET_SIZE as usize];
                let any = SocketAddrV4::new(std::net::Ipv4Addr::UNSPECIFIED, listen_port);
                write_sockaddr(bind_addr.as_mut_ptr(), &any);
                if bind(
                    socket,
                    bind_addr.as_ptr() as *const SOCKADDR,
                    SOCKADDR_INET_SIZE as i32,
                ) != 0
                {
                    fatal("bind");
                }

                let rio = load_function_table(socket);

                let memory_size = POOL_SLOTS * SLOT_SIZE
                    + POOL_SLOTS * ADDR_STRIDE
                    + destination_count * ADDR_STRIDE;
                // Zeroed and leaked, like the C# NativeMemory.AllocZeroed
                // block: the pool lives until process exit.
                let layout = std::alloc::Layout::from_size_align(memory_size, 16).unwrap();
                let memory = std::alloc::alloc_zeroed(layout);
                if memory.is_null() {
                    std::alloc::handle_alloc_error(layout);
                }

                for (index, destination) in destinations.iter().enumerate() {
                    write_sockaddr(memory.add(destination_addr_offset(index)), destination);
                }

                let register_buffer = rio.RIORegisterBuffer.expect("RIORegisterBuffer missing");
                let buffer_id = register_buffer(memory, memory_size as u32);
                if buffer_id == RIO_INVALID_BUFFERID {
                    fatal("RIORegisterBuffer");
                }

                let event = CreateEventW(std::ptr::null(), 0, 0, std::ptr::null());
                if event.is_null() {
                    fatal("CreateEventW");
                }
                let notification = RIO_NOTIFICATION_COMPLETION {
                    Type: RIO_EVENT_COMPLETION,
                    Anonymous: RIO_NOTIFICATION_COMPLETION_0 {
                        Event: RIO_NOTIFICATION_COMPLETION_0_0 {
                            EventHandle: event,
                            NotifyReset: 1,
                        },
                    },
                };

                let completion_queue_size =
                    (POSTED_RECEIVES + (POOL_SLOTS - POSTED_RECEIVES) * destination_count) as u32;
                let create_cq = rio
                    .RIOCreateCompletionQueue
                    .expect("RIOCreateCompletionQueue missing");
                let completion_queue = create_cq(completion_queue_size, &notification);
                if completion_queue == 0 {
                    fatal("RIOCreateCompletionQueue");
                }

                // args: socket, maxOutstandingReceive, maxReceiveDataBuffers,
                //       maxOutstandingSend, maxSendDataBuffers, receiveCQ,
                //       sendCQ, context
                let create_rq = rio
                    .RIOCreateRequestQueue
                    .expect("RIOCreateRequestQueue missing");
                let request_queue = create_rq(
                    socket,
                    POSTED_RECEIVES as u32,
                    1,
                    ((POOL_SLOTS - POSTED_RECEIVES) * destination_count) as u32,
                    1,
                    completion_queue,
                    completion_queue,
                    std::ptr::null(),
                );
                if request_queue == 0 {
                    fatal("RIOCreateRequestQueue");
                }

                let mut forwarder = RioForwarder {
                    receive_ex: rio.RIOReceiveEx.expect("RIOReceiveEx missing"),
                    send_ex: rio.RIOSendEx.expect("RIOSendEx missing"),
                    dequeue_completion: rio
                        .RIODequeueCompletion
                        .expect("RIODequeueCompletion missing"),
                    notify: rio.RIONotify.expect("RIONotify missing"),
                    completion_queue,
                    request_queue,
                    buffer_id,
                    event,
                    _memory: memory,
                    pending_sends: vec![0; POOL_SLOTS],
                    free_slots: Vec::with_capacity(POOL_SLOTS),
                    destination_count,
                    stats,
                };

                for slot in 0..POSTED_RECEIVES {
                    forwarder.post_receive(slot);
                }
                for slot in POSTED_RECEIVES..POOL_SLOTS {
                    forwarder.free_slots.push(slot as u32);
                }
                forwarder
            }
        }

        fn run(&mut self) -> ! {
            let mut results = [RIORESULT::default(); DEQUEUE_BATCH];
            loop {
                unsafe {
                    let mut count = (self.dequeue_completion)(
                        self.completion_queue,
                        results.as_mut_ptr(),
                        DEQUEUE_BATCH as u32,
                    );
                    if count == RIO_CORRUPT_CQ {
                        eprintln!("RIO completion queue corrupt");
                        exit(1);
                    }

                    if count == 0 {
                        // Queue is dry: arm the kernel notification, re-check
                        // for the completion that may have landed in between,
                        // then sleep.
                        (self.notify)(self.completion_queue);
                        count = (self.dequeue_completion)(
                            self.completion_queue,
                            results.as_mut_ptr(),
                            DEQUEUE_BATCH as u32,
                        );
                        if count == RIO_CORRUPT_CQ {
                            eprintln!("RIO completion queue corrupt");
                            exit(1);
                        }
                        if count == 0 {
                            WaitForSingleObject(self.event, 100);
                            continue;
                        }
                    }

                    for index in 0..count as usize {
                        let result = results[index];
                        if result.RequestContext & SEND_CONTEXT_BIT != 0 {
                            let slot = (result.RequestContext & !SEND_CONTEXT_BIT) as usize;
                            if result.Status == 0 {
                                self.stats.tx_packets.fetch_add(1, Ordering::Relaxed);
                                self.stats
                                    .tx_bytes
                                    .fetch_add(result.BytesTransferred as u64, Ordering::Relaxed);
                            }
                            self.pending_sends[slot] -= 1;
                            if self.pending_sends[slot] == 0 {
                                self.free_slots.push(slot as u32);
                            }
                        } else {
                            let slot = result.RequestContext as usize;
                            if result.Status != 0 {
                                self.post_receive(slot); // e.g. a truncated datagram; recycle
                                continue;
                            }
                            self.stats.rx_packets.fetch_add(1, Ordering::Relaxed);
                            self.stats
                                .rx_bytes
                                .fetch_add(result.BytesTransferred as u64, Ordering::Relaxed);

                            if let Some(free) = self.free_slots.pop() {
                                // Replacement receive comes from the free
                                // pool; the filled slot is sent from
                                // directly, zero copies.
                                self.post_receive(free as usize);
                                self.pending_sends[slot] = self.destination_count as u32;
                                for d in 0..self.destination_count {
                                    self.post_send(slot, result.BytesTransferred, d);
                                }
                            } else {
                                // Tx backpressure: drop this datagram on our
                                // counter and recycle its slot as the
                                // replacement receive.
                                self.stats.dropped.fetch_add(1, Ordering::Relaxed);
                                self.post_receive(slot);
                            }
                        }
                    }
                }
            }
        }

        fn post_receive(&mut self, slot: usize) {
            let data = RIO_BUF {
                BufferId: self.buffer_id,
                Offset: data_offset(slot) as u32,
                Length: SLOT_SIZE as u32,
            };
            let remote = RIO_BUF {
                BufferId: self.buffer_id,
                Offset: addr_offset(slot) as u32,
                Length: SOCKADDR_INET_SIZE,
            };
            let ok = unsafe {
                (self.receive_ex)(
                    self.request_queue,
                    &data,
                    1,
                    std::ptr::null(),
                    &remote,
                    std::ptr::null(),
                    std::ptr::null(),
                    0,
                    slot as *const core::ffi::c_void,
                )
            };
            if ok == 0 {
                fatal("RIOReceiveEx");
            }
        }

        fn post_send(&mut self, slot: usize, length: u32, destination: usize) {
            let data = RIO_BUF {
                BufferId: self.buffer_id,
                Offset: data_offset(slot) as u32, // sent straight from the receive slot
                Length: length,
            };
            let remote = RIO_BUF {
                BufferId: self.buffer_id,
                Offset: destination_addr_offset(destination) as u32,
                Length: SOCKADDR_INET_SIZE,
            };
            // RequestContext is 64-bit; usize is too on every platform RIO
            // exists on, so the tagged context survives the pointer cast.
            let context = slot as u64 | SEND_CONTEXT_BIT;
            let ok = unsafe {
                (self.send_ex)(
                    self.request_queue,
                    &data,
                    1,
                    std::ptr::null(),
                    &remote,
                    std::ptr::null(),
                    std::ptr::null(),
                    0,
                    context as usize as *const core::ffi::c_void,
                )
            };
            if ok == 0 {
                // Count the send as finished so the slot is not leaked.
                self.pending_sends[slot] -= 1;
                if self.pending_sends[slot] == 0 {
                    self.free_slots.push(slot as u32);
                }
            }
        }
    }

    /// Fetches the RIO function table via WSAIoctl; neither .NET nor libstd
    /// exposes RIO, so the table comes straight from the provider.
    unsafe fn load_function_table(socket: SOCKET) -> RIO_EXTENSION_FUNCTION_TABLE {
        let guid = WSAID_MULTIPLE_RIO;
        let mut table = RIO_EXTENSION_FUNCTION_TABLE::default();
        let mut bytes: u32 = 0;
        let result = WSAIoctl(
            socket,
            SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER,
            &guid as *const _ as *const core::ffi::c_void,
            std::mem::size_of_val(&guid) as u32,
            &mut table as *mut _ as *mut core::ffi::c_void,
            std::mem::size_of_val(&table) as u32,
            &mut bytes,
            std::ptr::null_mut(),
            None,
        );
        if result != 0 {
            fatal("WSAIoctl(SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER)");
        }
        table
    }

    /// Prints the same line shape as the C# rungs so the measurement scripts
    /// parse all rungs identically. Rust has no GC, so the alloc/gen0 fields
    /// the C# reporter prints are simply absent here.
    fn reporter(stats: Arc<Stats>, interval: Duration) {
        let mut previous_rx = 0u64;
        let mut previous_rx_bytes = 0u64;
        let mut previous_tx = 0u64;
        let mut previous_tx_bytes = 0u64;
        let mut previous_cpu = process_cpu_time();
        let mut last = Instant::now();
        loop {
            thread::sleep(interval);
            let seconds = last.elapsed().as_secs_f64();
            last = Instant::now();

            let rx = stats.rx_packets.load(Ordering::Relaxed);
            let rx_bytes = stats.rx_bytes.load(Ordering::Relaxed);
            let tx = stats.tx_packets.load(Ordering::Relaxed);
            let tx_bytes = stats.tx_bytes.load(Ordering::Relaxed);
            let dropped = stats.dropped.load(Ordering::Relaxed);
            let cpu = process_cpu_time();

            let rx_pps = (rx - previous_rx) as f64 / seconds;
            let rx_mbit = (rx_bytes - previous_rx_bytes) as f64 * 8.0 / seconds / 1_000_000.0;
            let tx_pps = (tx - previous_tx) as f64 / seconds;
            let tx_mbit = (tx_bytes - previous_tx_bytes) as f64 * 8.0 / seconds / 1_000_000.0;
            let cpu_pct = (cpu - previous_cpu).as_secs_f64() / seconds * 100.0;

            println!(
                "rx {:>11} pps {:>8.1} Mbit/s | tx {:>11} pps {:>8.1} Mbit/s | cpu {:>5.1}% | total rx {} tx {} drop {}",
                commas(rx_pps as u64),
                rx_mbit,
                commas(tx_pps as u64),
                tx_mbit,
                cpu_pct,
                commas(rx),
                commas(tx),
                commas(dropped),
            );

            previous_rx = rx;
            previous_rx_bytes = rx_bytes;
            previous_tx = tx;
            previous_tx_bytes = tx_bytes;
            previous_cpu = cpu;
        }
    }

    /// Process CPU time (kernel + user): the same quantity .NET reports as
    /// Process.TotalProcessorTime; both sit on GetProcessTimes.
    fn process_cpu_time() -> Duration {
        #[repr(C)]
        #[derive(Default, Clone, Copy)]
        struct FileTime {
            low: u32,
            high: u32,
        }
        extern "system" {
            fn GetCurrentProcess() -> isize;
            fn GetProcessTimes(
                handle: isize,
                creation: *mut FileTime,
                exit: *mut FileTime,
                kernel: *mut FileTime,
                user: *mut FileTime,
            ) -> i32;
        }
        unsafe {
            let mut creation = FileTime::default();
            let mut exited = FileTime::default();
            let mut kernel = FileTime::default();
            let mut user = FileTime::default();
            if GetProcessTimes(
                GetCurrentProcess(),
                &mut creation,
                &mut exited,
                &mut kernel,
                &mut user,
            ) == 0
            {
                return Duration::ZERO;
            }
            let ticks = |t: FileTime| ((t.high as u64) << 32) | t.low as u64;
            Duration::from_nanos((ticks(kernel) + ticks(user)) * 100)
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
}
