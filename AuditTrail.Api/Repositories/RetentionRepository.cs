using Dapper;
using Npgsql;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;

namespace AuditTrail.Api.Repositories;

/// <summary>retention_policies, legal_holds and the purge.</summary>
public sealed class RetentionRepository(NpgsqlDataSource db)
{
    private const string HoldColumns = "id, tenant_id, scope_type, scope_value, reason, created_by, created_at, released_at, released_by";

    public async Task<List<RetentionPolicy>> ListPoliciesAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<RetentionPolicy>(Database.Cmd(
            "SELECT tenant_id, category, retention_days, updated_at FROM retention_policies WHERE tenant_id = @tenantId ORDER BY category",
            new { tenantId }, ct: ct))).ToList();
    }

    public Task<RetentionPolicy> UpsertPolicyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, string category, int retentionDays, CancellationToken ct)
        => conn.QuerySingleAsync<RetentionPolicy>(Database.Cmd("""
            INSERT INTO retention_policies (tenant_id, category, retention_days) VALUES (@tenantId, @category, @retentionDays)
            ON CONFLICT (tenant_id, category) DO UPDATE SET retention_days = excluded.retention_days, updated_at = now()
            RETURNING tenant_id, category, retention_days, updated_at
            """, new { tenantId, category, retentionDays }, tx, ct));

    public async Task<List<LegalHold>> ListHoldsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<LegalHold>(Database.Cmd(
            $"SELECT {HoldColumns} FROM legal_holds WHERE tenant_id = @tenantId ORDER BY released_at IS NULL DESC, created_at DESC",
            new { tenantId }, ct: ct))).ToList();
    }

    /// <summary>
    /// Creates a hold. First takes FOR NO KEY UPDATE on the tenant row: this waits for any purge batch that is running for
    /// this tenant (purge holds FOR SHARE on the same row), and makes later purge batches wait until this hold is committed.
    /// That is how "a hold created while a purge is running" can never lose events. See purge_expired_events() in the migration.
    /// </summary>
    public async Task<LegalHold> InsertHoldAsync(NpgsqlConnection conn, NpgsqlTransaction tx, LegalHold hold, CancellationToken ct)
    {
        await conn.ExecuteAsync(Database.Cmd("SELECT 1 FROM tenants WHERE id = @TenantId FOR NO KEY UPDATE", hold, tx, ct));
        return await conn.QuerySingleAsync<LegalHold>(Database.Cmd($"""
            INSERT INTO legal_holds (id, tenant_id, scope_type, scope_value, reason, created_by)
            VALUES (@Id, @TenantId, @ScopeType, @ScopeValue, @Reason, @CreatedBy)
            RETURNING {HoldColumns}
            """, hold, tx, ct));
    }

    /// <summary>Active -> released. Returns null when the hold is unknown or already released.</summary>
    public Task<LegalHold?> ReleaseHoldAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid id, string releasedBy, CancellationToken ct)
        => conn.QuerySingleOrDefaultAsync<LegalHold?>(Database.Cmd($"""
            UPDATE legal_holds SET released_at = now(), released_by = @releasedBy
            WHERE tenant_id = @tenantId AND id = @id AND released_at IS NULL
            RETURNING {HoldColumns}
            """, new { tenantId, id, releasedBy }, tx, ct));

    public async Task<LegalHold?> GetHoldAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<LegalHold>(Database.Cmd(
            $"SELECT {HoldColumns} FROM legal_holds WHERE tenant_id = @tenantId AND id = @id", new { tenantId, id }, ct: ct));
    }

    /// <summary>
    /// Deletes ONE bounded batch of expired, un-held events (purge_expired_events in 001_schema.sql).
    /// Each call is its own short transaction, so locks are never held for long.
    /// </summary>
    public async Task<int> PurgeBatchAsync(Guid tenantId, int batchSize, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(Database.Cmd(
            "SELECT purge_expired_events(@tenantId, @batchSize)", new { tenantId, batchSize }, ct: ct));
    }
}
