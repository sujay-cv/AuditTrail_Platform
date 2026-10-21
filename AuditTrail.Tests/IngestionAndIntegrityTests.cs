using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuditTrail.Api.Services;
using Dapper;

namespace AuditTrail.Tests;

/// <summary>Ingestion against real PostgreSQL: idempotency, partial batches, concurrency, auth.</summary>
[Collection("api")]
public sealed class IngestionTests(ApiFixture fx)
{
    [Fact]
    public async Task Single_event_duplicate_and_conflict()
    {
        var t = await fx.CreateTenantAsync();
        var producer = fx.Producer(t.KeyA);
        var ev = Events.Valid("evt-1");

        var first = await producer.PostAsJsonAsync("/api/ingest/events", ev);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var seq = (await first.Json()).GetProperty("sequence").GetInt64();

        var retry = await producer.PostAsJsonAsync("/api/ingest/events", ev);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retryBody = await retry.Json();
        Assert.Equal("Duplicate", retryBody.GetProperty("status").GetString());
        Assert.Equal(seq, retryBody.GetProperty("sequence").GetInt64());   // original sequence

        ev["action"] = "record.deleted";
        var conflict = await producer.PostAsJsonAsync("/api/ingest/events", ev);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("event_id_conflict", (await conflict.Json()).GetProperty("code").GetString());

        await using var conn = fx.Connection();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM audit_events WHERE tenant_id = @Id", new { t.Id }));
    }

    [Fact]
    public async Task Mixed_batch_stores_valid_events_and_reports_each_one()
    {
        var t = await fx.CreateTenantAsync();
        var at = DateTime.UtcNow.AddMinutes(-1);
        var bad = Events.Valid("b-2", at);
        bad["category"] = "Nope";
        var conflicting = Events.Valid("b-1", at, resourceId: "OTHER");

        var response = await fx.Producer(t.KeyA).PostAsJsonAsync("/api/ingest/events/batch",
            new { events = new object[] { Events.Valid("b-1", at), bad, Events.Valid("b-1", at), conflicting, Events.Valid("b-3", at) } });

        Assert.Equal((HttpStatusCode)207, response.StatusCode);
        var statuses = (await response.Json()).GetProperty("results").EnumerateArray().Select(r => r.GetProperty("status").GetString()).ToArray();
        Assert.Equal(["Accepted", "Rejected", "Duplicate", "Conflict", "Accepted"], statuses);

        await using var conn = fx.Connection();
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM audit_events WHERE tenant_id = @Id", new { t.Id }));
    }

    [Fact]
    public async Task Empty_and_oversized_batches_are_rejected()
    {
        var t = await fx.CreateTenantAsync();
        var producer = fx.Producer(t.KeyA);
        Assert.Equal(HttpStatusCode.BadRequest, (await producer.PostAsJsonAsync("/api/ingest/events/batch", new { events = Array.Empty<object>() })).StatusCode);
        var tooMany = Enumerable.Range(0, 501).Select(i => Events.Valid($"x-{i}")).ToArray();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await producer.PostAsJsonAsync("/api/ingest/events/batch", new { events = tooMany })).StatusCode);
    }

    [Fact]
    public async Task Revoked_or_unknown_key_is_401()
    {
        var t = await fx.CreateTenantAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Producer(t.RevokedKey).PostAsJsonAsync("/api/ingest/events", Events.Valid("e"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Producer("nope").PostAsJsonAsync("/api/ingest/events", Events.Valid("e"))).StatusCode);
    }

    [Fact]
    public async Task Redacted_metadata_is_never_stored()
    {
        var t = await fx.CreateTenantAsync();
        await fx.Producer(t.KeyA).PostAsJsonAsync("/api/ingest/events", Events.Valid("r-1", metadata: new { password = "hunter2", nested = new { cardNumber = "4111" } }));
        await using var conn = fx.Connection();
        var stored = await conn.ExecuteScalarAsync<string>("SELECT metadata::text FROM audit_events WHERE tenant_id = @Id", new { t.Id });
        Assert.DoesNotContain("hunter2", stored);
        Assert.DoesNotContain("4111", stored);
        Assert.Contains("[REDACTED]", stored);
    }

    [Fact]
    public async Task Concurrent_ingestion_gives_a_gap_free_sequence_and_a_valid_chain()
    {
        var t = await fx.CreateTenantAsync();
        var a = fx.Producer(t.KeyA);
        var b = fx.Producer(t.KeyB);

        // 40 concurrent requests from two producers, plus the SAME EventId sent concurrently by 5 requests.
        var tasks = Enumerable.Range(0, 40).Select(i => Events.PostBatchAsync(i % 2 == 0 ? a : b,
                Enumerable.Range(0, 25).Select(j => Events.Valid($"c-{i}-{j}"))))
            .Concat(Enumerable.Range(0, 5).Select(_ => Events.PostBatchAsync(a, [Events.Valid("same-id", DateTime.UtcNow.Date)])));
        await Task.WhenAll(tasks);

        await using var conn = fx.Connection();
        var sequences = (await conn.QueryAsync<long>("SELECT sequence FROM audit_events WHERE tenant_id = @Id ORDER BY sequence", new { t.Id })).ToArray();
        Assert.Equal(1001, sequences.Length);
        Assert.Equal(Enumerable.Range(1, 1001).Select(i => (long)i), sequences);   // 1..N, no gaps, no duplicates

        var verify = await (await fx.Auditor(t.Id).PostAsJsonAsync("/api/integrity/verify", new { })).Json();
        Assert.True(verify.GetProperty("ok").GetBoolean());
        Assert.Equal(1001, verify.GetProperty("verifiedCount").GetInt64());
    }

    [Fact]
    public async Task Database_rejects_update_and_direct_delete_of_events()
    {
        var t = await fx.CreateTenantAsync();
        await fx.Producer(t.KeyA).PostAsJsonAsync("/api/ingest/events", Events.Valid("ao-1"));
        await using var conn = fx.Connection();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => conn.ExecuteAsync("UPDATE audit_events SET action = 'x.y' WHERE tenant_id = @Id", new { t.Id }));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => conn.ExecuteAsync("DELETE FROM audit_events WHERE tenant_id = @Id", new { t.Id }));
    }
}

/// <summary>Tamper detection at the exact sequence.</summary>
[Collection("api")]
public sealed class IntegrityTests(ApiFixture fx)
{
    private async Task<TestTenant> TenantWithEventsAsync(int count)
    {
        var t = await fx.CreateTenantAsync();
        await Events.PostBatchAsync(fx.Producer(t.KeyA), Enumerable.Range(1, count).Select(i => Events.Valid($"i-{i}")));
        return t;
    }

    /// <summary>Simulates a DBA bypassing the append-only trigger.</summary>
    private async Task TamperAsync(string sql, Guid tenantId)
    {
        await using var conn = fx.Connection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("SET LOCAL session_replication_role = replica", transaction: tx);
        await conn.ExecuteAsync(sql, new { tenantId }, tx);
        await tx.CommitAsync();
    }

    private async Task<JsonElement> VerifyAsync(Guid tenantId)
        => await (await fx.Auditor(tenantId).PostAsJsonAsync("/api/integrity/verify", new { })).Json();

    [Fact]
    public async Task Modified_row_is_detected_at_its_sequence()
    {
        var t = await TenantWithEventsAsync(20);
        await TamperAsync("UPDATE audit_events SET actor_id = 'user:mallory' WHERE tenant_id = @tenantId AND sequence = 7", t.Id);
        var r = await VerifyAsync(t.Id);
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Equal(7, r.GetProperty("firstBrokenSequence").GetInt64());
        Assert.Equal("content_modified", r.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Deleted_row_is_detected_at_its_sequence()
    {
        var t = await TenantWithEventsAsync(20);
        await TamperAsync("DELETE FROM audit_events WHERE tenant_id = @tenantId AND sequence = 12", t.Id);
        var r = await VerifyAsync(t.Id);
        Assert.Equal(12, r.GetProperty("firstBrokenSequence").GetInt64());
        Assert.Equal("missing", r.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Swapped_pair_is_detected_at_the_first_of_the_two()
    {
        var t = await TenantWithEventsAsync(20);
        await TamperAsync("""
            UPDATE audit_events SET sequence = -1 WHERE tenant_id = @tenantId AND sequence = 5;
            UPDATE audit_events SET sequence = 5  WHERE tenant_id = @tenantId AND sequence = 6;
            UPDATE audit_events SET sequence = 6  WHERE tenant_id = @tenantId AND sequence = -1;
            """, t.Id);
        var r = await VerifyAsync(t.Id);
        Assert.Equal(5, r.GetProperty("firstBrokenSequence").GetInt64());
    }

    [Fact]
    public async Task Verification_starts_from_a_checkpoint()
    {
        var t = await TenantWithEventsAsync(10);
        Assert.True(await fx.Service<IntegrityService>().CreateCheckpointAsync(t.Id, CancellationToken.None));
        await Events.PostBatchAsync(fx.Producer(t.KeyA), Enumerable.Range(11, 5).Select(i => Events.Valid($"i-{i}")));

        var r = await (await fx.Auditor(t.Id).PostAsJsonAsync("/api/integrity/verify", new { fromSequence = 11 })).Json();
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal(10, r.GetProperty("checkpointUsed").GetProperty("sequence").GetInt64());
        Assert.Equal(5, r.GetProperty("verifiedCount").GetInt64());
    }
}
