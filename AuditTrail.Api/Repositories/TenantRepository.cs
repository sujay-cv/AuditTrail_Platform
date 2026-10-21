using Dapper;
using Npgsql;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;

namespace AuditTrail.Api.Repositories;

/// <summary>Tenants and producers (read-only at runtime; created by the seeder).</summary>
public sealed class TenantRepository(NpgsqlDataSource db)
{
    /// <summary>The internal producer used when the platform audits its own actions (exports, holds, retention).</summary>
    public const string SystemProducerName = "audit-platform";

    public async Task<Producer?> FindProducerByKeyHashAsync(string keyHash, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Producer>(Database.Cmd(
            "SELECT id, tenant_id, name, api_key_hash, status FROM producers WHERE api_key_hash = @keyHash",
            new { keyHash }, ct: ct));
    }

    public async Task<bool> ExistsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(Database.Cmd(
            "SELECT EXISTS (SELECT 1 FROM tenants WHERE id = @tenantId)", new { tenantId }, ct: ct));
    }

    public async Task<List<Tenant>> ListAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Tenant>(Database.Cmd(
            "SELECT id, name, created_at, redaction_keys FROM tenants ORDER BY name", ct: ct))).ToList();
    }

    public async Task<string[]> GetRedactionKeysAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<string[]>(Database.Cmd(
            "SELECT redaction_keys FROM tenants WHERE id = @tenantId", new { tenantId }, ct: ct)) ?? [];
    }

    public async Task<List<Producer>> ListProducersAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Producer>(Database.Cmd(
            "SELECT id, tenant_id, name, status FROM producers WHERE tenant_id = @tenantId ORDER BY name",
            new { tenantId }, ct: ct))).ToList();
    }

    public Task<Guid> GetSystemProducerIdAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
        => conn.ExecuteScalarAsync<Guid>(Database.Cmd(
            "SELECT id FROM producers WHERE tenant_id = @tenantId AND name = @name",
            new { tenantId, name = SystemProducerName }, tx, ct));
}
