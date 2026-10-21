using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

// ------------------------------------------------------------------------------------------------------------
// Producer simulator: several internal systems (payments, identity, HR, admin...) sending audit events to the API.
//
//   dotnet run --project AuditTrail.Simulator -- --tenants 2 --producers 6 --events 20000 --batch-size 100 --concurrency 32
//        --duplicates 5 --late 5 --invalid 2 --retry-timeouts --hot-tenant 60 --base-url http://localhost:8080
//
// Concurrency is bounded with Parallel.ForEachAsync(MaxDegreeOfParallelism = --concurrency): the work (a list of
// requests) is known up front, so a simple bounded parallel loop is enough. A Channel<T> would only be needed for an
// open-ended producer/consumer stream.
// ------------------------------------------------------------------------------------------------------------

var opt = SimOptions.Parse(args);
Console.WriteLine($"Simulating {opt.Events} events, {opt.Tenants} tenant(s), {opt.Producers} producer(s), batch {opt.BatchSize}, " +
                  $"concurrency {opt.Concurrency}, duplicates {opt.DuplicatesPct}%, late {opt.LatePct}%, invalid {opt.InvalidPct}%, " +
                  $"hot tenant {opt.HotTenantPct}%, retry-timeouts {opt.RetryTimeouts} -> {opt.BaseUrl}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\nCtrl+C: stopping, waiting for in-flight requests..."); };

// One shared HttpClient for the whole run (connection pooling). Timeouts are per request (see Sender).
using var http = new HttpClient { BaseAddress = new Uri(opt.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };
var stats = new Stats();
var sender = new Sender(http, stats, opt);
var work = Workload.Build(opt);
Console.WriteLine($"Prepared {work.Count} requests ({work.Sum(w => w.Events.Count)} events incl. resent duplicates).");

var clock = Stopwatch.StartNew();
try
{
    await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = opt.Concurrency, CancellationToken = cts.Token },
        async (item, ct) =>
        {
            if (item.BurstPauseMs > 0) await Task.Delay(item.BurstPauseMs, ct);   // makes the traffic bursty
            await sender.SendAsync(item, ct);
        });
}
catch (OperationCanceledException)
{
    Console.WriteLine($"Cancelled. Requests still in flight at cancellation: {stats.InFlightAtCancel ?? stats.InFlight}");
}
stats.Print(clock.Elapsed);
return 0;

// ============================================================================================================

sealed record SimOptions(
    string BaseUrl, int Tenants, int Producers, int Events, int BatchSize, int Concurrency,
    int DuplicatesPct, int LatePct, int InvalidPct, bool RetryTimeouts, int HotTenantPct, int SinglePct, string RunId)
{
    public static SimOptions Parse(string[] args)
    {
        string Get(string name, string fallback)
        {
            var i = Array.IndexOf(args, "--" + name);
            return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : fallback;
        }
        int Int(string name, int fallback) => int.Parse(Get(name, fallback.ToString()));

        return new SimOptions(
            BaseUrl: Get("base-url", "http://localhost:8080"),
            Tenants: Math.Clamp(Int("tenants", 2), 1, 2),
            Producers: Math.Clamp(Int("producers", 6), 1, 8),
            Events: Int("events", 2000),
            BatchSize: Math.Clamp(Int("batch-size", 100), 1, 500),
            Concurrency: Math.Clamp(Int("concurrency", 16), 1, 256),
            DuplicatesPct: Int("duplicates", 0),
            LatePct: Int("late", 0),
            InvalidPct: Int("invalid", 0),
            RetryTimeouts: args.Contains("--retry-timeouts"),
            HotTenantPct: Int("hot-tenant", 0),
            SinglePct: Int("single", 10),
            RunId: Get("run-id", DateTime.UtcNow.ToString("MMddHHmmss")));
    }
}

/// <summary>Demo producer keys created by the API's seeder (local demo only, not secrets).</summary>
static class DemoProducers
{
    public static readonly (string Name, string Key)[][] ByTenant =
    [
        [("payments-service", "acme-payments-key"), ("identity-service", "acme-identity-key"), ("hr-service", "acme-hr-key"), ("admin-console", "acme-admin-key")],
        [("claims-service", "globex-claims-key"), ("identity-service", "globex-identity-key"), ("billing-service", "globex-billing-key"), ("admin-console", "globex-admin-key")],
    ];
}

sealed record WorkItem(string ProducerKey, bool Single, List<JsonObject> Events, int BurstPauseMs);

static class Workload
{
    private static readonly string[] Categories = ["Security", "Access", "DataChange", "Configuration", "Financial"];
    private static readonly string[] Outcomes = ["Success", "Success", "Success", "Failure", "Denied"];
    private static readonly Dictionary<string, string[]> Actions = new()
    {
        ["Security"] = ["user.login", "user.logout", "user.mfa.challenged"],
        ["Access"] = ["record.viewed", "report.downloaded"],
        ["DataChange"] = ["customer.updated", "invoice.created"],
        ["Configuration"] = ["user.role.granted", "feature.flag.changed"],
        ["Financial"] = ["payment.approved", "refund.issued"],
    };

    public static List<WorkItem> Build(SimOptions o)
    {
        var random = new Random(7);
        var perTenant = Math.Max(1, o.Producers / o.Tenants);
        var producers = Enumerable.Range(0, o.Tenants)
            .Select(t => DemoProducers.ByTenant[t].Take(Math.Min(perTenant, 4)).ToArray()).ToArray();

        // 1. Generate events and decide which producer sends each one (hot tenant gets --hot-tenant % of the traffic).
        var byProducer = new Dictionary<string, List<JsonObject>>();
        for (var i = 0; i < o.Events; i++)
        {
            // --hot-tenant N: N% of events go to tenant 0 (shows contention on its chain); otherwise spread evenly.
            var tenant = o.Tenants == 1 ? 0
                : o.HotTenantPct > 0 ? (random.Next(100) < o.HotTenantPct ? 0 : 1)
                : random.Next(o.Tenants);
            var (name, key) = producers[tenant][random.Next(producers[tenant].Length)];

            var ev = MakeEvent(random, $"sim-{o.RunId}-{name}-{i:D7}", random.Next(100) < o.LatePct);
            if (random.Next(100) < o.InvalidPct) Corrupt(ev, random);
            if (!byProducer.TryGetValue(key, out var list)) byProducer[key] = list = [];
            list.Add(ev);
        }

        // 2. Duplicates: re-send a copy of an already generated event (same EventId + payload) later in the run.
        foreach (var list in byProducer.Values)
        {
            var copies = list.Where(_ => random.Next(100) < o.DuplicatesPct).Select(e => (JsonObject)e.DeepClone()).ToList();
            list.AddRange(copies);
        }

        // 3. Cut into requests: some single events, the rest batches.
        var work = new List<WorkItem>();
        foreach (var (key, list) in byProducer)
        {
            var rest = new List<JsonObject>();
            foreach (var e in list)
            {
                if (random.Next(100) < o.SinglePct) work.Add(new WorkItem(key, true, [e], Burst(random)));
                else rest.Add(e);
            }
            foreach (var chunk in rest.Chunk(o.BatchSize)) work.Add(new WorkItem(key, false, chunk.ToList(), Burst(random)));
        }
        return work.OrderBy(_ => random.Next()).ToList();   // interleave producers and tenants
    }

    /// <summary>Mostly no pause; now and then a short pause, so requests arrive in bursts.</summary>
    private static int Burst(Random r) => r.Next(10) == 0 ? r.Next(50, 300) : 0;

    private static JsonObject MakeEvent(Random r, string eventId, bool late)
    {
        var category = Categories[r.Next(Categories.Length)];
        var occurredAt = late ? DateTime.UtcNow.AddHours(-r.Next(2, 48)) : DateTime.UtcNow.AddSeconds(-r.Next(0, 5));
        return new JsonObject
        {
            ["eventId"] = eventId,
            ["occurredAt"] = occurredAt.ToString("O"),
            ["actor"] = new JsonObject { ["type"] = "User", ["id"] = $"user:sim{r.Next(1, 40)}", ["ip"] = $"10.1.{r.Next(0, 255)}.{r.Next(1, 254)}" },
            ["action"] = Actions[category][r.Next(Actions[category].Length)],
            ["resource"] = new JsonObject { ["type"] = "Record", ["id"] = $"REC-{r.Next(1, 100000)}" },
            ["outcome"] = Outcomes[r.Next(Outcomes.Length)],
            ["category"] = category,
            ["metadata"] = new JsonObject { ["source"] = "simulator", ["password"] = "will-be-redacted" },
        };
    }

    private static void Corrupt(JsonObject e, Random r)
    {
        switch (r.Next(4))
        {
            case 0: e.Remove("actor"); break;
            case 1: e["category"] = "Gossip"; break;
            case 2: e["occurredAt"] = DateTime.UtcNow.AddDays(3).ToString("O"); break;
            default: e["action"] = "NOT A DOTTED VERB"; break;
        }
    }
}

sealed class Sender(HttpClient http, Stats stats, SimOptions opt)
{
    private const int MaxAttempts = 4;

    public async Task SendAsync(WorkItem item, CancellationToken ct)
    {
        var path = item.Single ? "api/ingest/events" : "api/ingest/events/batch";
        object body = item.Single ? item.Events[0] : new JsonObject { ["events"] = new JsonArray(item.Events.Select(e => (JsonNode)e.DeepClone()).ToArray()) };
        Interlocked.Add(ref stats.EventsSent, item.Events.Count);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // --retry-timeouts: 10% of first attempts give up after 15ms. The server may still commit them;
            // the retry then reports "Duplicate" (same sequence) instead of storing them twice.
            var timeout = opt.RetryTimeouts && attempt == 1 && Random.Shared.Next(10) == 0 ? TimeSpan.FromMilliseconds(15) : TimeSpan.FromSeconds(30);
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(timeout);

            var watch = Stopwatch.StartNew();
            Interlocked.Increment(ref stats.InFlight);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
                request.Headers.Add("X-Api-Key", item.ProducerKey);
                using var response = await http.SendAsync(request, attemptCts.Token);
                stats.Latency(watch.Elapsed);

                if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

                await stats.RecordAsync(response, item.Single, ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < MaxAttempts &&
                                       ex is OperationCanceledException or HttpRequestException)
            {
                // Ingestion is idempotent on EventId, so retrying the exact same request is always safe.
                Interlocked.Increment(ref stats.Retries);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt) * (0.8 + Random.Shared.NextDouble() * 0.4)), ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                stats.InFlightAtCancel ??= stats.InFlight;
                throw new OperationCanceledException(ct);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref stats.FailedRequests);
                Console.WriteLine($"Request failed after {attempt} attempts: {ex.Message}");
                return;
            }
            finally
            {
                Interlocked.Decrement(ref stats.InFlight);
            }
        }
    }
}

sealed class Stats
{
    public long EventsSent, Accepted, Duplicates, Conflicts, Retries, FailedRequests, InFlight;
    public long? InFlightAtCancel;
    private readonly ConcurrentDictionary<string, long> _rejected = new();
    private readonly ConcurrentBag<double> _latenciesMs = [];

    public void Latency(TimeSpan t) => _latenciesMs.Add(t.TotalMilliseconds);

    public async Task RecordAsync(HttpResponseMessage response, bool single, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        if (single)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Created: Interlocked.Increment(ref Accepted); break;
                case HttpStatusCode.OK: Interlocked.Increment(ref Duplicates); break;
                case HttpStatusCode.Conflict: Interlocked.Increment(ref Conflicts); break;
                default: _rejected.AddOrUpdate(root.TryGetProperty("code", out var c) ? c.GetString()! : $"http_{(int)response.StatusCode}", 1, (_, n) => n + 1); break;
            }
            return;
        }

        if (!root.TryGetProperty("results", out var results))
        {
            _rejected.AddOrUpdate(root.TryGetProperty("code", out var c) ? c.GetString()! : $"http_{(int)response.StatusCode}", 1, (_, n) => n + 1);
            return;
        }
        foreach (var r in results.EnumerateArray())
        {
            switch (r.GetProperty("status").GetString())
            {
                case "Accepted": Interlocked.Increment(ref Accepted); break;
                case "Duplicate": Interlocked.Increment(ref Duplicates); break;
                case "Conflict": Interlocked.Increment(ref Conflicts); break;
                default: _rejected.AddOrUpdate(r.GetProperty("reason").GetString() ?? "unknown", 1, (_, n) => n + 1); break;
            }
        }
    }

    public void Print(TimeSpan elapsed)
    {
        var latencies = _latenciesMs.OrderBy(x => x).ToArray();
        double P(double p) => latencies.Length == 0 ? 0 : latencies[(int)Math.Min(latencies.Length - 1, Math.Ceiling(p * latencies.Length) - 1)];

        Console.WriteLine();
        Console.WriteLine("================ Simulator report ================");
        Console.WriteLine($"Events sent        : {EventsSent}");
        Console.WriteLine($"Accepted           : {Accepted}");
        Console.WriteLine($"Duplicates         : {Duplicates}");
        Console.WriteLine($"Conflicts          : {Conflicts}");
        Console.WriteLine($"Rejected           : {_rejected.Values.Sum()}");
        foreach (var (reason, count) in _rejected.OrderByDescending(r => r.Value))
            Console.WriteLine($"   - {reason,-24}: {count}");
        Console.WriteLine($"Retries            : {Retries}");
        Console.WriteLine($"Failed requests    : {FailedRequests}");
        Console.WriteLine($"Elapsed            : {elapsed.TotalSeconds:0.0}s");
        Console.WriteLine($"Throughput         : {(Accepted + Duplicates) / Math.Max(0.001, elapsed.TotalSeconds):0} events/s");
        Console.WriteLine($"Request latency    : p50 {P(0.50):0} ms, p95 {P(0.95):0} ms ({latencies.Length} requests)");
        Console.WriteLine("Next: verify the chains are gap-free -> POST /api/integrity/verify (or the console's Integrity Check screen).");
    }
}
