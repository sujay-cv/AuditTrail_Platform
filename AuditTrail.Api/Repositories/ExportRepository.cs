using Dapper;
using Npgsql;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;

namespace AuditTrail.Api.Repositories;

/// <summary>
/// export_jobs. Every state change is ONE conditional UPDATE, so it is atomic, and the database trigger
/// (trg_export_jobs_guard) rejects any illegal transition even if the C# code had a bug.
/// Worker updates also check "lease_owner = @owner", so a worker that lost its lease cannot overwrite the new owner's progress.
/// </summary>
public sealed class ExportRepository(NpgsqlDataSource db)
{
    private const string Columns =
        "id, tenant_id, filter::text AS filter, format, status, requested_by, idempotency_key, request_hash, snapshot_sequence, " +
        "lease_owner, lease_expires_at, attempts, resume_cursor::text AS resume_cursor, row_count, file_bytes, file_location, " +
        "checksum, error, created_at, completed_at";

    // ---------- API side ----------

    /// <summary>Inserts the job, or returns the existing one with the same (tenant, Idempotency-Key). Item2 = true when newly created.</summary>
    public async Task<(ExportJob Job, bool Created)> InsertOrGetAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ExportJob job, CancellationToken ct)
    {
        var inserted = await conn.QuerySingleOrDefaultAsync<ExportJob>(Database.Cmd($"""
            INSERT INTO export_jobs (id, tenant_id, filter, format, status, requested_by, idempotency_key, request_hash, snapshot_sequence)
            VALUES (@Id, @TenantId, @Filter::jsonb, @Format, 'Queued', @RequestedBy, @IdempotencyKey, @RequestHash, @SnapshotSequence)
            ON CONFLICT (tenant_id, idempotency_key) DO NOTHING
            RETURNING {Columns}
            """, job, tx, ct));
        if (inserted is not null) return (inserted, true);

        var existing = await conn.QuerySingleAsync<ExportJob>(Database.Cmd(
            $"SELECT {Columns} FROM export_jobs WHERE tenant_id = @TenantId AND idempotency_key = @IdempotencyKey", job, tx, ct));
        return (existing, false);
    }

    public async Task<ExportJob?> GetAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ExportJob>(Database.Cmd(
            $"SELECT {Columns} FROM export_jobs WHERE tenant_id = @tenantId AND id = @id", new { tenantId, id }, ct: ct));
    }

    public async Task<List<ExportJob>> ListAsync(Guid tenantId, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<ExportJob>(Database.Cmd(
            $"SELECT {Columns} FROM export_jobs WHERE tenant_id = @tenantId ORDER BY created_at DESC LIMIT @limit",
            new { tenantId, limit }, ct: ct))).ToList();
    }

    /// <summary>Queued/Running -> Cancelled. Returns null if the job is not cancellable (or does not exist).</summary>
    public async Task<ExportJob?> CancelAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid id, CancellationToken ct)
        => await conn.QuerySingleOrDefaultAsync<ExportJob>(Database.Cmd($"""
            UPDATE export_jobs SET status = 'Cancelled', completed_at = now(), lease_owner = NULL, lease_expires_at = NULL
            WHERE tenant_id = @tenantId AND id = @id AND status IN ('Queued', 'Running')
            RETURNING {Columns}
            """, new { tenantId, id }, tx, ct));

    // ---------- Worker side ----------

    /// <summary>
    /// Running jobs whose lease ran out (worker crashed / was killed) go back to Queued so any worker can resume them,
    /// unless they have used all attempts, in which case they become Failed.
    /// </summary>
    public async Task RecoverExpiredLeasesAsync(int maxAttempts, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(Database.Cmd("""
            UPDATE export_jobs
            SET status = CASE WHEN attempts >= @maxAttempts THEN 'Failed' ELSE 'Queued' END,
                error = CASE WHEN attempts >= @maxAttempts THEN 'Gave up after ' || attempts || ' attempts' ELSE error END,
                completed_at = CASE WHEN attempts >= @maxAttempts THEN now() ELSE NULL END,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE status = 'Running' AND lease_expires_at < now()
            """, new { maxAttempts }, ct: ct));
    }

    /// <summary>
    /// Claim the oldest Queued job. FOR UPDATE SKIP LOCKED means two workers never claim the same job and never wait for each other.
    /// Uses index ix_export_jobs_lease (status, lease_expires_at).
    /// </summary>
    public async Task<ExportJob?> ClaimNextAsync(string owner, int leaseSeconds, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ExportJob>(Database.Cmd($"""
            UPDATE export_jobs
            SET status = 'Running', lease_owner = @owner, lease_expires_at = now() + make_interval(secs => @leaseSeconds), attempts = attempts + 1
            WHERE id = (SELECT id FROM export_jobs WHERE status = 'Queued' ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING {Columns}
            """, new { owner, leaseSeconds }, ct: ct));
    }

    /// <summary>Records progress AND renews the lease. False = we no longer own the job (cancelled or lease lost): stop.</summary>
    public async Task<bool> SaveProgressAsync(Guid id, string owner, string cursorJson, long rowCount, long fileBytes, int leaseSeconds, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(Database.Cmd("""
            UPDATE export_jobs
            SET resume_cursor = @cursorJson::jsonb, row_count = @rowCount, file_bytes = @fileBytes,
                lease_expires_at = now() + make_interval(secs => @leaseSeconds)
            WHERE id = @id AND status = 'Running' AND lease_owner = @owner
            """, new { id, owner, cursorJson, rowCount, fileBytes, leaseSeconds }, ct: ct)) == 1;
    }

    public async Task<bool> CompleteAsync(Guid id, string owner, string fileLocation, string checksum, long rowCount, long fileBytes, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(Database.Cmd("""
            UPDATE export_jobs
            SET status = 'Completed', file_location = @fileLocation, checksum = @checksum, row_count = @rowCount, file_bytes = @fileBytes,
                completed_at = now(), lease_owner = NULL, lease_expires_at = NULL
            WHERE id = @id AND status = 'Running' AND lease_owner = @owner
            """, new { id, owner, fileLocation, checksum, rowCount, fileBytes }, ct: ct)) == 1;
    }

    /// <summary>Graceful shutdown: hand the job back (Running -> Queued) so another worker resumes it immediately.</summary>
    public async Task ReleaseAsync(Guid id, string owner, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(Database.Cmd(
            "UPDATE export_jobs SET status = 'Queued', lease_owner = NULL, lease_expires_at = NULL WHERE id = @id AND status = 'Running' AND lease_owner = @owner",
            new { id, owner }, ct: ct));
    }

    public async Task<string?> GetStatusAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(Database.Cmd("SELECT status FROM export_jobs WHERE id = @id", new { id }, ct: ct));
    }

    /// <summary>Completed jobs older than the retention window -> Expired. Returns their file paths so the worker can delete them.</summary>
    public async Task<List<string>> ExpireCompletedAsync(int olderThanHours, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var files = await conn.QueryAsync<string?>(Database.Cmd("""
            UPDATE export_jobs SET status = 'Expired'
            WHERE status = 'Completed' AND completed_at < now() - make_interval(hours => @olderThanHours)
            RETURNING file_location
            """, new { olderThanHours }, ct: ct));
        return files.OfType<string>().ToList();
    }
}
