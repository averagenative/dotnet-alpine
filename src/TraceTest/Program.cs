using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Self-calling client — used by the background traffic generator
builder.Services.AddHttpClient("self", (_, client) =>
{
    var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5080";
    var baseUrl = urls.Split(';')[0].Replace("+", "localhost");
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});

// External client — generates outbound spans visible to OneAgent
builder.Services.AddHttpClient("external", client =>
{
    client.BaseAddress = new Uri("https://httpbin.org/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddHostedService<TrafficGeneratorService>();

var app = builder.Build();

// ── Endpoints ─────────────────────────────────────────────────────────────────

app.MapGet("/", () => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTimeOffset.UtcNow,
    version = "1.0.0"
}));

// Outbound HTTP — exercises OneAgent exit-span detection
app.MapGet("/api/http", async (IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("external");
    try
    {
        var response = await client.GetAsync("get");
        var body = await response.Content.ReadAsStringAsync();
        return Results.Ok(new { upstream_status = (int)response.StatusCode, bytes = body.Length });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// CPU work — exercises method-level profiling
app.MapGet("/api/cpu", (int? iterations) =>
{
    var n = Math.Clamp(iterations ?? 1_000_000, 1, 10_000_000);
    var result = 0UL;
    for (var i = 0; i < n; i++) result += (ulong)i;
    return Results.Ok(new { iterations = n, result, timestamp = DateTimeOffset.UtcNow });
});

// Async delay — exercises async/await span continuations
app.MapGet("/api/delay", async (int? ms) =>
{
    var delay = Math.Clamp(ms ?? 200, 10, 5000);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await Task.Delay(delay);
    sw.Stop();
    return Results.Ok(new { requested_ms = delay, actual_ms = sw.ElapsedMilliseconds });
});

// Chain — sequential async work + outbound HTTP in one trace
app.MapGet("/api/chain", async (IHttpClientFactory factory) =>
{
    await Task.Delay(Random.Shared.Next(50, 200));
    var client = factory.CreateClient("external");
    var response = await client.GetAsync("uuid");
    var body = await response.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(body);
    var uuid = doc.RootElement.GetProperty("uuid").GetString();
    return Results.Ok(new { uuid, timestamp = DateTimeOffset.UtcNow });
});

// Error — generates non-2xx spans for error-rate visibility
app.MapGet("/api/error", (int? code) => Results.StatusCode(code ?? 500));

app.Run();

// ── Background traffic generator ──────────────────────────────────────────────

/// <summary>
/// Fires periodic self-requests so OneAgent has a continuous stream of
/// inbound + outbound spans to instrument even without external load.
/// </summary>
public class TrafficGeneratorService(
    IHttpClientFactory factory,
    IHostApplicationLifetime lifetime,
    ILogger<TrafficGeneratorService> logger) : BackgroundService
{
    private static readonly string[] Endpoints =
    [
        "/api/delay",
        "/api/delay?ms=500",
        "/api/cpu",
        "/api/cpu?iterations=500000",
        "/api/chain",
        "/api/http",
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait until Kestrel is fully bound before making self-calls
        var appStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.ApplicationStarted.Register(() => appStarted.TrySetResult());
        await appStarted.Task.WaitAsync(stoppingToken);
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        var client = factory.CreateClient("self");
        logger.LogInformation("Traffic generator started — base URL: {BaseUrl}", client.BaseAddress);

        while (!stoppingToken.IsCancellationRequested)
        {
            var endpoint = Endpoints[Random.Shared.Next(Endpoints.Length)];
            try
            {
                var response = await client.GetAsync(endpoint, stoppingToken);
                logger.LogDebug("self -> {Endpoint} {Status}", endpoint, (int)response.StatusCode);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Traffic generator error on {Endpoint}: {Message}", endpoint, ex.Message);
            }

            // Random jitter so traces don't all land in the same bucket
            await Task.Delay(Random.Shared.Next(3_000, 10_000), stoppingToken);
        }
    }
}
