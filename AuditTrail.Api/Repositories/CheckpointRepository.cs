using Dapper;
using Npgsql;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;

namespace AuditTrail.Api.Repositories;

/// <summary>integrity_checkpoints: "the chain was verified from 1 up to Sequence and ended with Hash".</summary>
public sealed class CheckpointRepository(NpgsqlDataSource db)
{
    private const string Columns = "id, tenant_id, sequence, hash, created_at";

    /// <summary>The newest checkpoint strictly before <paramref name="sequence"/>, i.e. a trusted starting point for walking up to it.</summary>
    public async Task<IntegrityCheckpoint?> LatestBeforeAsync(Guid tenantId, long sequence, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<IntegrityCheckpoint>(Database.Cmd(
            $"SELECT {Columns} FROM integrity_checkpoints WHERE tenant_id = @tenantId AND sequence < @sequence ORDER BY sequence DESC LIMIT 1",
            new { tenantId, sequence }, ct: ct));
    }

    public async Task<IntegrityCheckpoint?> LatestAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<IntegrityCheckpoint>(Database.Cmd(
            $"SELECT {Columns} FROM integrity_checkpoints WHERE tenant_id = @tenantId ORDER BY sequence DESC LIMIT 1",
            new { tenantId }, ct: ct));
    }

    public async Task InsertAsync(Guid tenantId, long sequence, byte[] hash, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(Database.Cmd(
            "INSERT INTO integrity_checkpoints (tenant_id, sequence, hash) VALUES (@tenantId, @sequence, @hash) ON CONFLICT (tenant_id, sequence) DO NOTHING",
            new { tenantId, sequence, hash }, ct: ct));
    }
}
