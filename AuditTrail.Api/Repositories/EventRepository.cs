using System.Diagnostics;
using System.Text;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;
using AuditTrail.Api.Services;

namespace AuditTrail.Api.Repositories;

/// <summary>Search result row: an event without metadata, plus the producer's name.</summary>
public sealed class EventListRow
{
    public Guid Id { get; set; }
    public string EventId { get; set; } = "";
    public long Sequence { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string ActorType { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string Action { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Category { get; set; } = "";
    public Guid ProducerId { get; set; }
    public string ProducerName { get; set; } = "";
}

/// <summary>What is left of an event after retention deleted it: enough to keep walking the chain.</summary>
public sealed class PurgedEvent
{
    public long Sequence { get; set; }
    public byte[] PrevHash { get; set; } = [];
    public byte[] Hash { get; set; } = [];
}

/// <summary>
/// All SQL for audit_events. AppendAsync is the ONLY code that inserts events.
/// </summary>
public sealed class EventRepository(NpgsqlDataSource db, AuditMetrics metrics, TimeProvider clock)
{
    private const string EventColumns =
        "id, tenant_id, producer_id, event_id, sequence, occurred_at, received_at, actor_type, actor_id, actor_ip, actor_user_agent, " +
        "action, resource_type, resource_id, outcome, category, metadata::text AS metadata, payload_hash, hash_version, prev_hash, hash";

    private sealed class ExistingRow
    {
        public string EventId { get; set; } = "";
        public long Sequence { get; set; }
        public byte[] PayloadHash { get; set; } = [];
    }

    // ------------------------------------------------------------------------------------------------
    // WRITE: append to the tenant's chain
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Appends events to the tenant's hash chain inside the caller's transaction. EventIds in <paramref name="events"/> must be unique.
    ///
    /// How sequence + hash are assigned without races or gaps, across any number of API instances:
    ///   1. SELECT ... FROM chain_heads WHERE tenant_id = @t FOR UPDATE
    ///        -> row lock = "the chain lock". Other writers for the SAME tenant wait here. Other tenants are not affected.
    ///   2. Look up which EventIds already exist (idempotency). No race: nobody else can insert for this tenant while we hold the lock.
    ///   3. In memory: sequence = head + 1, + 2, ...; hash = SHA-256(prev_hash || canonical(event)).
    ///   4. COPY the new rows in, then move chain_heads to the new last sequence/hash.
    ///   5. The CALLER commits: rows + new head become visible together and the lock is released.
    /// If the process crashes anywhere before COMMIT, PostgreSQL rolls everything back (no rows, head unchanged, lock released):
    /// no gap, no half-written chain. The producer gets no response, retries with the same EventIds, and they are stored once.
    ///
    /// Throughput: one transaction per request per tenant at a time. A batch of 500 pays for ONE lock round trip,
    /// which is why batching is how a hot tenant gets throughput.
    /// </summary>
    public async Task<AppendResult[]> AppendAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid producerId, IReadOnlyList<NewEvent> events, CancellationToken ct)
    {
        var results = new AppendResult[events.Count];
        if (events.Count == 0) return results;

        // Do not queue forever behind a hot tenant's lock: after 5s fail with 503 and let the producer retry.
        await conn.ExecuteAsync(Database.Cmd("SET LOCAL lock_timeout = '5s'", tx: tx, ct: ct));

        // 1. Chain lock
        var wait = Stopwatch.StartNew();
        var head = await conn.QuerySingleOrDefaultAsync<ChainHead>(Database.Cmd(
            "SELECT tenant_id, last_sequence, last_hash, last_received_at FROM chain_heads WHERE tenant_id = @tenantId FOR UPDATE",
            new { tenantId }, tx, ct)) ?? throw new InvalidOperationException($"Tenant {tenantId} has no chain head.");
        metrics.ChainLockWait(wait.Elapsed);

        // 2. Idempotency: which of these EventIds are already stored for this producer?
        var existing = (await conn.QueryAsync<ExistingRow>(Database.Cmd(
            "SELECT event_id, sequence, payload_hash FROM audit_events " +
            "WHERE tenant_id = @tenantId AND producer_id = @producerId AND event_id = ANY(@eventIds)",
            new { tenantId, producerId, eventIds = events.Select(e => e.EventId).ToArray() }, tx, ct)))
            .ToDictionary(r => r.EventId);

        // 3. Assign sequence + hash
        var newRows = new List<AuditEvent>();
        var sequence = head.LastSequence;
        var prevHash = head.LastHash;
        var lastReceivedAt = head.LastReceivedAt;
        var now = CanonicalSerializer.TruncateToMicroseconds(clock.GetUtcNow().UtcDateTime);

        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (existing.TryGetValue(e.EventId, out var stored))
            {
                var same = stored.PayloadHash.AsSpan().SequenceEqual(e.PayloadHash);
                results[i] = new AppendResult(same ? AppendOutcome.Duplicate : AppendOutcome.Conflict, stored.Sequence);
                continue;
            }

            // ReceivedAt must strictly increase along the chain, even if two API servers' clocks differ slightly.
            var candidate = e.ReceivedAtOverride is { } seeded ? CanonicalSerializer.TruncateToMicroseconds(seeded) : now;
            var receivedAt = candidate > lastReceivedAt ? candidate : lastReceivedAt.AddTicks(10);
            lastReceivedAt = receivedAt;
            sequence++;

            var row = new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ProducerId = producerId,
                EventId = e.EventId,
                Sequence = sequence,
                OccurredAt = e.OccurredAt,
                ReceivedAt = receivedAt,
                ActorType = e.ActorType,
                ActorId = e.ActorId,
                ActorIp = e.ActorIp,
                ActorUserAgent = e.ActorUserAgent,
                Action = e.Action,
                ResourceType = e.ResourceType,
                ResourceId = e.ResourceId,
                Outcome = e.Outcome,
                Category = e.Category,
                Metadata = e.MetadataJson,
                PayloadHash = e.PayloadHash,
                HashVersion = CanonicalSerializer.Version,
                PrevHash = prevHash,
            };
            row.Hash = CanonicalSerializer.ChainHash(prevHash, CanonicalSerializer.EventBytes(row));
            prevHash = row.Hash;

            newRows.Add(row);
            results[i] = new AppendResult(AppendOutcome.Accepted, sequence);
        }

        if (newRows.Count == 0) return results;

        // 4. Insert + move the head (same transaction)
        await CopyAsync(conn, newRows, ct);
        await conn.ExecuteAsync(Database.Cmd(
            "UPDATE chain_heads SET last_sequence = @sequence, last_hash = @prevHash, last_received_at = @lastReceivedAt WHERE tenant_id = @tenantId",
            new { sequence, prevHash, lastReceivedAt, tenantId }, tx, ct));

        return results;
    }

    /// <summary>Binary COPY: the fastest way to insert many rows. Runs inside the connection's open transaction.</summary>
    private static async Task CopyAsync(NpgsqlConnection conn, List<AuditEvent> rows, CancellationToken ct)
    {
        await using var copy = await conn.BeginBinaryImportAsync(
            "COPY audit_events (id, tenant_id, producer_id, event_id, sequence, occurred_at, received_at, actor_type, actor_id, actor_ip, " +
            "actor_user_agent, action, resource_type, resource_id, outcome, category, metadata, payload_hash, hash_version, prev_hash, hash) " +
            "FROM STDIN (FORMAT BINARY)", ct);

        foreach (var r in rows)
        {
            await copy.StartRowAsync(ct);
            await copy.WriteAsync(r.Id, NpgsqlDbType.Uuid, ct);
            await copy.WriteAsync(r.TenantId, NpgsqlDbType.Uuid, ct);
            await copy.WriteAsync(r.ProducerId, NpgsqlDbType.Uuid, ct);
            await copy.WriteAsync(r.EventId, NpgsqlDbType.Text, ct);
            await copy.WriteAsync(r.Sequence, NpgsqlDbType.Bigint, ct);
            await copy.WriteAsync(r.OccurredAt, NpgsqlDbType.TimestampTz, ct);
            await copy.WriteAsync(r.ReceivedAt, NpgsqlDbType.TimestampTz, ct);
            await copy.WriteAsync(r.ActorType, NpgsqlDbType.Text, ct);
            await copy.WriteAsync(r.ActorId, NpgsqlDbType.Text, ct);
            if (r.ActorIp is null) await copy.WriteNullAsync(ct); else await copy.WriteAsync(r.ActorIp, NpgsqlDbType.Text, ct);
            if (r.ActorUserAgent is null) await copy.WriteNullAsync(ct); else await copy.WriteAsync(r.ActorUserAgent, NpgsqlDbType.Text, ct);
            await copy.WriteAsync(r.Action, NpgsqlDbType.Text, ct);
            await copy.WriteAsync(r.ResourceType, NpgsqlDbType.Text, ct);
            await copy.WriteAsync(r.ResourceId, NpgsqlDbType.Text, ct);
            await copy.WriteAsync(r.Outcome, NpgsqlDbType.Text, ct);
            await copy.WriteAsync(r.Category, NpgsqlDbType.Text, ct);
            await copy.WriteAsync(r.Metadata, NpgsqlDbType.Jsonb, ct);
            await copy.WriteAsync(r.PayloadHash, NpgsqlDbType.Bytea, ct);
            await copy.WriteAsync(r.HashVersion, NpgsqlDbType.Smallint, ct);
            await copy.WriteAsync(r.PrevHash, NpgsqlDbType.Bytea, ct);
            await copy.WriteAsync(r.Hash, NpgsqlDbType.Bytea, ct);
        }
        await copy.CompleteAsync(ct);
    }

    // ------------------------------------------------------------------------------------------------
    // READ
    // ------------------------------------------------------------------------------------------------

    public async Task<long> GetHeadSequenceAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await GetHeadSequenceAsync(conn, null, tenantId, ct);
    }

    public Task<long> GetHeadSequenceAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, CancellationToken ct)
        => conn.ExecuteScalarAsync<long>(Database.Cmd(
            "SELECT coalesce((SELECT last_sequence FROM chain_heads WHERE tenant_id = @tenantId), 0)", new { tenantId }, tx, ct));

    public async Task<ChainHead?> GetHeadAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ChainHead>(Database.Cmd(
            "SELECT tenant_id, last_sequence, last_hash, last_received_at FROM chain_heads WHERE tenant_id = @tenantId", new { tenantId }, ct: ct));
    }

    /// <summary>One event of THIS tenant. An id that belongs to another tenant simply is not found (404, not 403).</summary>
    public async Task<(AuditEvent Event, string ProducerName)?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<AuditEvent>(Database.Cmd(
            $"SELECT {EventColumns} FROM audit_events WHERE tenant_id = @tenantId AND id = @id", new { tenantId, id }, ct: ct));
        if (row is null) return null;
        var producerName = await conn.ExecuteScalarAsync<string>(Database.Cmd(
            "SELECT name FROM producers WHERE tenant_id = @tenantId AND id = @producerId", new { tenantId, producerId = row.ProducerId }, ct: ct));
        return (row, producerName ?? "");
    }

    /// <summary>
    /// Keyset (cursor) page ordered by (occurred_at, sequence), oldest first or newest first.
    /// Uses index ix_events_time (tenant_id, occurred_at, sequence); PostgreSQL scans it backwards for newest first.
    /// maxSequence pins the result set to what existed when the first page was read, so pages stay stable while ingestion continues.
    /// </summary>
    public async Task<List<EventListRow>> SearchAsync(Guid tenantId, EventFilter filter, EventCursor? after, long maxSequence, int limit, bool newestFirst, CancellationToken ct)
    {
        var (where, args) = BuildWhere(tenantId, filter, after, maxSequence, newestFirst);
        args.Add("limit", limit);
        var direction = newestFirst ? "DESC" : "ASC";

        var sql = $"""
            SELECT e.id, e.event_id, e.sequence, e.occurred_at, e.received_at, e.actor_type, e.actor_id, e.action,
                   e.resource_type, e.resource_id, e.outcome, e.category, e.producer_id, p.name AS producer_name
            FROM audit_events e
            JOIN producers p ON p.tenant_id = e.tenant_id AND p.id = e.producer_id
            WHERE {where}
            ORDER BY e.occurred_at {direction}, e.sequence {direction}
            LIMIT @limit
            """;

        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<EventListRow>(Database.Cmd(sql, args, ct: ct))).ToList();
    }

    /// <summary>Same query as search (always oldest first) but returns full events. Used by exports, one bounded chunk at a time.</summary>
    public async Task<List<AuditEvent>> ReadChunkAsync(Guid tenantId, EventFilter filter, EventCursor? after, long maxSequence, int limit, CancellationToken ct)
    {
        var (where, args) = BuildWhere(tenantId, filter, after, maxSequence, newestFirst: false);
        args.Add("limit", limit);
        var columns = string.Join(", ", EventColumns.Split(", ").Select(c => "e." + c));

        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<AuditEvent>(Database.Cmd(
            $"SELECT {columns} FROM audit_events e WHERE {where} ORDER BY e.occurred_at, e.sequence LIMIT @limit", args, ct: ct))).ToList();
    }

    private static (string Where, DynamicParameters Args) BuildWhere(Guid tenantId, EventFilter f, EventCursor? after, long maxSequence, bool newestFirst)
    {
        var sql = new StringBuilder("e.tenant_id = @tenantId AND e.occurred_at >= @from AND e.occurred_at < @to AND e.sequence <= @maxSequence");
        var args = new DynamicParameters(new { tenantId, from = f.From, to = f.To, maxSequence });

        if (f.ActorId is not null) { sql.Append(" AND e.actor_id = @actorId"); args.Add("actorId", f.ActorId); }
        if (f.ActionPrefix is not null) { sql.Append(@" AND e.action LIKE @actionPattern ESCAPE '\'"); args.Add("actionPattern", EscapeLike(f.ActionPrefix) + "%"); }
        if (f.ResourceType is not null) { sql.Append(" AND e.resource_type = @resourceType"); args.Add("resourceType", f.ResourceType); }
        if (f.ResourceId is not null) { sql.Append(" AND e.resource_id = @resourceId"); args.Add("resourceId", f.ResourceId); }
        if (f.Outcome is not null) { sql.Append(" AND e.outcome = @outcome"); args.Add("outcome", f.Outcome.ToString()); }
        if (f.Category is not null) { sql.Append(" AND e.category = @category"); args.Add("category", f.Category.ToString()); }
        if (f.ProducerId is not null) { sql.Append(" AND e.producer_id = @producerId"); args.Add("producerId", f.ProducerId); }
        if (after is not null)
        {
            // Keyset: the rows strictly after the last row of the previous page, in the requested direction.
            sql.Append(newestFirst
                ? " AND (e.occurred_at, e.sequence) < (@afterOccurredAt, @afterSequence)"
                : " AND (e.occurred_at, e.sequence) > (@afterOccurredAt, @afterSequence)");
            args.Add("afterOccurredAt", after.OccurredAt);
            args.Add("afterSequence", after.Sequence);
        }
        return (sql.ToString(), args);
    }

    private static string EscapeLike(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>
    /// Everything in a sequence range, for verification: full events (to recompute hashes) and skeletons of purged events.
    /// </summary>
    public async Task<(List<AuditEvent> Events, List<PurgedEvent> Purged)> ReadChainRangeAsync(Guid tenantId, long fromSequence, long toSequence, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var events = (await conn.QueryAsync<AuditEvent>(Database.Cmd(
            $"SELECT {EventColumns} FROM audit_events WHERE tenant_id = @tenantId AND sequence BETWEEN @fromSequence AND @toSequence ORDER BY sequence",
            new { tenantId, fromSequence, toSequence }, ct: ct))).ToList();
        var purged = (await conn.QueryAsync<PurgedEvent>(Database.Cmd(
            "SELECT sequence, prev_hash, hash FROM purged_events WHERE tenant_id = @tenantId AND sequence BETWEEN @fromSequence AND @toSequence ORDER BY sequence",
            new { tenantId, fromSequence, toSequence }, ct: ct))).ToList();
        return (events, purged);
    }
}
