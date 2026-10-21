using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuditTrail.Api.Services;
using Dapper;

namespace AuditTrail.Tests;

/// <summary>Search, tenant isolation, exports and retention against real PostgreSQL.</summary>
[Collection("api")]
public sealed class QueryExportRetentionTests(ApiFixture fx)
{
    private static string Range(DateTime from, DateTime to) => $"from={Events.Iso(from)}&to={Events.Iso(to)}";

    [Fact]
    public async Task Search_requires_a_bounded_time_range_and_a_valid_cursor()
    {
        var t = await fx.CreateTenantAsync();
        var auditor = fx.Auditor(t.Id);
        Assert.Equal("time_range_required", (await (await auditor.GetAsync("/api/events")).Json()).GetProperty("code").GetString());
        Assert.Equal("time_range_too_large", (await (await auditor.GetAsync($"/api/events?{Range(DateTime.UtcNow.AddDays(-91), DateTime.UtcNow)}")).Json()).GetProperty("code").GetString());
        Assert.Equal("invalid_cursor", (await (await auditor.GetAsync($"/api/events?{Range(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow)}&cursor=abc.def")).Json()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Search_filters_and_cursor_pages_stay_stable_while_inserts_continue()
    {
        var t = await fx.CreateTenantAsync();
        var producer = fx.Producer(t.KeyA);
        var start = DateTime.UtcNow.AddHours(-2);
        await Events.PostBatchAsync(producer, Enumerable.Range(0, 30).Select(i =>
            Events.Valid($"s-{i}", start.AddMinutes(i), category: i % 3 == 0 ? "Security" : "Access", action: i % 2 == 0 ? "user.login" : "record.viewed")));

        var auditor = fx.Auditor(t.Id);
        var range = Range(start.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));

        var filtered = await (await auditor.GetAsync($"/api/events?{range}&category=Security&actionPrefix=user.")).Json();
        Assert.Equal(5, filtered.GetProperty("items").GetArrayLength());   // i in {0,6,12,18,24}

        var page1 = await (await auditor.GetAsync($"/api/events?{range}&limit=10")).Json();
        // New events arrive between pages, including a late one that sorts INTO page 2's range.
        await Events.PostBatchAsync(producer, [Events.Valid("late-1", start.AddMinutes(15).AddSeconds(30)), Events.Valid("new-1")]);

        var seen = page1.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("eventId").GetString()).ToList();
        var cursor = page1.GetProperty("nextCursor").GetString();
        while (cursor is not null)
        {
            var page = await (await auditor.GetAsync($"/api/events?{range}&limit=10&cursor={Uri.EscapeDataString(cursor)}")).Json();
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("eventId").GetString()));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        Assert.Equal(30, seen.Count);                    // exactly the snapshot of the first page
        Assert.Equal(30, seen.Distinct().Count());
        Assert.DoesNotContain("late-1", seen);

        // Newest first: same rows, reverse order, and the cursor works in that direction too.
        var newest = new List<string?>();
        string? next = null;
        do
        {
            var page = await (await auditor.GetAsync($"/api/events?{range}&limit=10&newestFirst=true" + (next is null ? "" : $"&cursor={Uri.EscapeDataString(next)}"))).Json();
            newest.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("eventId").GetString()));
            next = page.GetProperty("nextCursor").GetString();
        } while (next is not null);
        Assert.Equal("new-1", newest[0]);
        Assert.Equal("s-0", newest[^1]);
        Assert.Equal(newest.Count, newest.Distinct().Count());

        // A cursor issued for one direction is rejected for the other.
        var firstNewest = await (await auditor.GetAsync($"/api/events?{range}&limit=5&newestFirst=true")).Json();
        var wrongDirection = await auditor.GetAsync($"/api/events?{range}&limit=5&cursor={Uri.EscapeDataString(firstNewest.GetProperty("nextCursor").GetString()!)}");
        Assert.Equal("cursor_filter_mismatch", (await wrongDirection.Json()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Event_of_another_tenant_is_404_not_403()
    {
        var a = await fx.CreateTenantAsync();
        var b = await fx.CreateTenantAsync();
        var created = await (await fx.Producer(a.KeyA).PostAsJsonAsync("/api/ingest/events", Events.Valid("shared-id"))).Json();
        await fx.Producer(b.KeyA).PostAsJsonAsync("/api/ingest/events", Events.Valid("shared-id"));   // same EventId, other tenant: fine

        var search = await (await fx.Auditor(a.Id).GetAsync($"/api/events?{Range(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(1))}")).Json();
        var id = search.GetProperty("items")[0].GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.OK, (await fx.Auditor(a.Id).GetAsync($"/api/events/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fx.Auditor(b.Id).GetAsync($"/api/events/{id}")).StatusCode);
        Assert.Equal(1, created.GetProperty("sequence").GetInt64());
    }

    [Fact]
    public async Task Auditor_cannot_change_retention_or_holds()
    {
        var t = await fx.CreateTenantAsync();
        var auditor = fx.Auditor(t.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PutAsJsonAsync("/api/retention/policies/Access", new { retentionDays = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsJsonAsync("/api/legal-holds", new { scopeType = "Actor", scopeValue = "x", reason = "r" })).StatusCode);
    }

    private static readonly DateTime ExportTo = DateTime.UtcNow.AddHours(1);

    /// <summary>The processor claims ANY tenant's queued job; finish leftovers from other tests so ours is next.</summary>
    private async Task DrainExportQueueAsync()
    {
        var processor = fx.Service<ExportProcessor>();
        while (await processor.RunOnceAsync("drain", CancellationToken.None)) { }
    }

    private async Task<JsonElement> CreateExportAsync(TestTenant t, string key, DateTime from)
    {
        var client = fx.Auditor(t.Id);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/exports")
        {
            Content = JsonContent.Create(new { filter = new { from, to = ExportTo }, format = "Ndjson" }),
        };
        request.Headers.Add("Idempotency-Key", key);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        return await response.Json();
    }

    [Fact]
    public async Task Export_is_idempotent_streams_and_resumes_after_a_crash_without_duplicates()
    {
        var t = await fx.CreateTenantAsync();
        await DrainExportQueueAsync();
        var start = DateTime.UtcNow.AddHours(-1);
        await Events.PostBatchAsync(fx.Producer(t.KeyA), Enumerable.Range(0, 35).Select(i => Events.Valid($"x-{i:D2}", start.AddSeconds(i))));

        var job = await CreateExportAsync(t, "key-1", start.AddMinutes(-1));
        var again = await CreateExportAsync(t, "key-1", start.AddMinutes(-1));
        Assert.Equal(job.GetProperty("id").GetString(), again.GetProperty("id").GetString());
        var id = Guid.Parse(job.GetProperty("id").GetString()!);

        // Simulate a worker that wrote ONE chunk (10 rows, chunk size in tests) plus some unsaved garbage, then crashed.
        var processor = fx.Service<ExportProcessor>();
        using (var crash = new CancellationTokenSource())
        {
            var chunks = 0;
            try { await processor.RunOnceAsync("worker-1", crash.Token, () => { if (++chunks == 1) crash.Cancel(); }); }
            catch (OperationCanceledException) { }
        }
        await using (var conn = fx.Connection())
        {
            // A real crash would not release the job: put it back to Running with an EXPIRED lease, and append garbage.
            await conn.ExecuteAsync("UPDATE export_jobs SET status = 'Running', lease_owner = 'worker-1', lease_expires_at = now() - interval '1 minute' WHERE id = @id", new { id });
            var part = Directory.GetFiles(fx.ExportDirectory, $"{id}.ndjson.part", SearchOption.AllDirectories).Single();
            await File.AppendAllTextAsync(part, "{\"garbage\":\"written after the last saved progress\"}\n");
        }

        Assert.True(await processor.RunOnceAsync("worker-2", CancellationToken.None));   // recovers the lease and resumes

        var done = await (await fx.Auditor(t.Id).GetAsync($"/api/exports/{id}")).Json();
        Assert.Equal("Completed", done.GetProperty("status").GetString());
        Assert.Equal(2, done.GetProperty("attempts").GetInt32());

        var download = await fx.Auditor(t.Id).GetAsync($"/api/exports/{id}/download");
        var body = await download.Content.ReadAsStringAsync();
        var lines = body.TrimEnd('\n').Split('\n');
        var eventIds = lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("eventId").GetString()).ToList();
        Assert.Equal(Enumerable.Range(0, 35).Select(i => $"x-{i:D2}"), eventIds.Where(e => e!.StartsWith("x-")));
        Assert.Equal(eventIds.Count, eventIds.Distinct().Count());
        Assert.Equal(done.GetProperty("checksum").GetString(), download.Headers.GetValues("X-Checksum-SHA256").Single());
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))), done.GetProperty("checksum").GetString());

        // Terminal job: cancel is 409 with the current status.
        var cancel = await fx.Auditor(t.Id).PostAsync($"/api/exports/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        Assert.Equal("Completed", (await cancel.Json()).GetProperty("currentStatus").GetString());
    }

    [Fact]
    public async Task Export_of_zero_rows_completes_and_cancelled_job_is_never_processed()
    {
        var t = await fx.CreateTenantAsync();
        var empty = await CreateExportAsync(t, "empty", DateTime.UtcNow.AddDays(-1));
        await DrainExportQueueAsync();
        var done = await (await fx.Auditor(t.Id).GetAsync($"/api/exports/{empty.GetProperty("id").GetString()}")).Json();
        Assert.Equal("Completed", done.GetProperty("status").GetString());
        Assert.Equal(0, done.GetProperty("rowCount").GetInt64());

        var job = await CreateExportAsync(t, "to-cancel", DateTime.UtcNow.AddDays(-1));
        var cancel = await fx.Auditor(t.Id).PostAsync($"/api/exports/{job.GetProperty("id").GetString()}/cancel", null);
        Assert.Equal("Cancelled", (await cancel.Json()).GetProperty("status").GetString());

        await using var conn = fx.Connection();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>   // terminal states never change, enforced by the database
            conn.ExecuteAsync("UPDATE export_jobs SET status = 'Queued' WHERE id = @id", new { id = Guid.Parse(job.GetProperty("id").GetString()!) }));
    }

    [Fact]
    public async Task Retention_deletes_only_expired_unheld_events_and_the_chain_still_verifies()
    {
        var t = await fx.CreateTenantAsync();
        var old = DateTime.UtcNow.AddDays(-100);
        await Events.PostBatchAsync(fx.Producer(t.KeyA),
        [
            Events.Valid("old-1", old, actorId: "user:alice"),
            Events.Valid("old-2", old.AddMinutes(1), actorId: "user:held"),
            Events.Valid("old-security", old.AddMinutes(2), category: "Security"),
            Events.Valid("recent", DateTime.UtcNow.AddDays(-1)),
        ]);

        var admin = fx.Auditor(t.Id, "ComplianceAdmin");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/retention/policies/Access", new { retentionDays = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/retention/policies/Security", new { retentionDays = 2555 })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/legal-holds", new { scopeType = "Actor", scopeValue = "user:held", reason = "litigation" })).StatusCode);

        var run = await (await admin.PostAsync("/api/retention/run", null)).Json();
        Assert.Equal(1, run.GetProperty("deleted").GetInt32());

        await using var conn = fx.Connection();
        var remaining = (await conn.QueryAsync<string>("SELECT event_id FROM audit_events WHERE tenant_id = @Id AND event_id NOT LIKE 'platform-%'", new { t.Id })).Order().ToArray();
        Assert.Equal(["old-2", "old-security", "recent"], remaining);

        var verify = await (await admin.PostAsJsonAsync("/api/integrity/verify", new { })).Json();
        Assert.True(verify.GetProperty("ok").GetBoolean());
        Assert.Equal(1, verify.GetProperty("purgedSkeletons").GetInt64());

        // The platform audited itself in the tenant's own chain.
        var actions = (await conn.QueryAsync<string>("SELECT action FROM audit_events WHERE tenant_id = @Id AND event_id LIKE 'platform-%'", new { t.Id })).ToArray();
        Assert.Contains("retention.policy.updated", actions);
        Assert.Contains("legal_hold.created", actions);
        Assert.Contains("retention.purge.completed", actions);
    }

    [Fact]
    public async Task Legal_hold_created_while_a_purge_batch_is_open_protects_its_events()
    {
        var t = await fx.CreateTenantAsync();
        var old = DateTime.UtcNow.AddDays(-100);
        await Events.PostBatchAsync(fx.Producer(t.KeyA), Enumerable.Range(0, 5).Select(i => Events.Valid($"o-{i}", old.AddMinutes(i), actorId: "user:target")));
        await fx.Auditor(t.Id, "ComplianceAdmin").PutAsJsonAsync("/api/retention/policies/Access", new { retentionDays = 30 });

        // 1. A hold is being created but NOT yet committed (holds the tenant row lock).
        await using var holdConn = fx.Connection();
        await holdConn.OpenAsync();
        await using var holdTx = await holdConn.BeginTransactionAsync();
        await holdConn.ExecuteAsync("SELECT 1 FROM tenants WHERE id = @Id FOR NO KEY UPDATE", new { t.Id }, holdTx);
        await holdConn.ExecuteAsync("INSERT INTO legal_holds (id, tenant_id, scope_type, scope_value, reason, created_by) VALUES (gen_random_uuid(), @Id, 'Actor', 'user:target', 'race', 'test')", new { t.Id }, holdTx);

        // 2. A purge starts: it must WAIT for the hold transaction, then see the hold.
        var purge = Task.Run(async () =>
        {
            await using var c = fx.Connection();
            return await c.ExecuteScalarAsync<int>("SELECT purge_expired_events(@Id, 100)", new { t.Id });
        });
        await Task.Delay(500);
        Assert.False(purge.IsCompleted);

        await holdTx.CommitAsync();
        Assert.Equal(0, await purge);   // nothing deleted
    }
}
