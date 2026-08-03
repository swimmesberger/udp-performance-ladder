using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace UdpBench;

/// <summary>
/// Long-running control API so the generator box is deployed once and
/// benchmark runs are triggered remotely. One run at a time; a run starts
/// an in-process sink (unless disabled), blasts the target, and exposes
/// both summaries as JSON. Set UDPBENCH_API_TOKEN to require a bearer token.
/// </summary>
public static class ServeCommand
{
    public static int Run(string[] args)
    {
        int port = 5080;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port":
                    port = int.Parse(args[++i]);
                    break;
                default:
                    Console.Error.WriteLine($"unknown argument '{args[i]}'");
                    return 1;
            }
        }

        string? token = Environment.GetEnvironmentVariable("UDPBENCH_API_TOKEN");

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        WebApplication app = builder.Build();

        if (!string.IsNullOrEmpty(token))
        {
            app.Use(async (context, next) =>
            {
                // /healthz stays open so the container healthcheck (and any
                // monitoring) needs no credentials; it exposes nothing.
                bool isHealth = context.Request.Path.StartsWithSegments("/healthz");
                if (!isHealth && context.Request.Headers.Authorization != $"Bearer {token}")
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                await next();
            });
        }

        var runs = new ConcurrentDictionary<string, BenchmarkRun>();
        var gate = new SemaphoreSlim(1, 1);

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/runs", () => Results.Ok(
            runs.Values.OrderByDescending(r => r.StartedUtc).Select(r => r.Snapshot())));

        app.MapGet("/runs/{id}", (string id) =>
            runs.TryGetValue(id, out BenchmarkRun? run)
                ? Results.Ok(run.Snapshot())
                : Results.NotFound());

        app.MapPost("/runs", (RunRequest request) =>
        {
            IPEndPoint target;
            try
            {
                if (string.IsNullOrEmpty(request.Target))
                {
                    return Results.BadRequest(new { error = "target is required" });
                }
                target = EndPoints.Resolve(request.Target);
            }
            catch (Exception e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
            if (request.Size < 8)
            {
                return Results.BadRequest(new { error = "size must be at least 8" });
            }
            if (request.SendDurationSeconds <= 0)
            {
                return Results.BadRequest(new { error = "sendDurationSeconds must be > 0" });
            }
            if (!gate.Wait(0))
            {
                return Results.Conflict(new { error = "a run is already in progress" });
            }

            var run = BenchmarkRun.Start(request, target, onFinished: () => gate.Release());
            runs[run.Id] = run;
            return Results.Accepted($"/runs/{run.Id}", run.Snapshot());
        });

        Console.WriteLine(
            $"udpbench control API on http://*:{port}" +
            (string.IsNullOrEmpty(token)
                ? " (no auth; set UDPBENCH_API_TOKEN to require a bearer token)"
                : ", bearer token required"));
        app.Run($"http://*:{port}");
        return 0;
    }

    /// <summary>
    /// Container healthcheck: the runtime image ships no curl or wget, so
    /// the binary probes its own /healthz endpoint. /healthz is exempt from
    /// the bearer token so this needs no credentials.
    /// </summary>
    public static int CheckHealth(string[] args)
    {
        int port = 5080;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port")
            {
                port = int.Parse(args[++i]);
            }
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            HttpResponseMessage response = http
                .GetAsync($"http://127.0.0.1:{port}/healthz")
                .GetAwaiter().GetResult();
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }
}

public sealed record RunRequest(
    string? Target,
    int Size = 32,
    long Rate = 0,
    int SendDurationSeconds = 10,
    bool Sink = true,
    int SinkPort = 6000,
    int? SinkDurationSeconds = null);

public sealed class BenchmarkRun
{
    private readonly object _lock = new();
    private readonly RunRequest _request;
    private string _status = "running";
    private string? _error;
    private SendResult? _send;
    private SinkResult? _sink;
    private DateTime? _completedUtc;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    private BenchmarkRun(RunRequest request) => _request = request;

    public static BenchmarkRun Start(RunRequest request, IPEndPoint target, Action onFinished)
    {
        var run = new BenchmarkRun(request);
        _ = Task.Run(async () =>
        {
            try
            {
                Task<SinkResult>? sinkTask = null;
                if (request.Sink)
                {
                    // The sink outlives the sender so it catches the tail of the run.
                    int sinkDuration = request.SinkDurationSeconds ?? request.SendDurationSeconds + 5;
                    var sinkOptions = new SinkOptions(request.SinkPort, sinkDuration);
                    sinkTask = Task.Run(() => UdpSink.Run(sinkOptions, progress: null, CancellationToken.None));
                    await Task.Delay(500); // let the sink bind before load starts
                }

                var sendOptions = new SendOptions(target, request.Size, request.Rate, request.SendDurationSeconds);
                SendResult send = await Task.Run(
                    () => UdpSender.Run(sendOptions, progress: null, CancellationToken.None));
                SinkResult? sink = sinkTask is null ? null : await sinkTask;

                lock (run._lock)
                {
                    run._send = send;
                    run._sink = sink;
                    run._status = "completed";
                    run._completedUtc = DateTime.UtcNow;
                }
            }
            catch (Exception e)
            {
                lock (run._lock)
                {
                    run._status = "failed";
                    run._error = e.Message;
                    run._completedUtc = DateTime.UtcNow;
                }
            }
            finally
            {
                onFinished();
            }
        });
        return run;
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                id = Id,
                status = _status,
                startedUtc = StartedUtc,
                completedUtc = _completedUtc,
                error = _error,
                request = _request,
                send = _send,
                sink = _sink,
            };
        }
    }
}
