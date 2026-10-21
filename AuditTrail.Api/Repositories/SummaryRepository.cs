using Dapper;
using Npgsql;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;

namespace AuditTrail.Api.Repositories;

public sealed class CategoryOutcomeRow { public string Category { get; set; } = ""; public string Outcome { get; set; } = ""; public long Count { get; set; } }
public sealed class DeniedActorRow { public string ActorType { get; set; } = ""; public string ActorId { get; set; } = ""; public long Count { get; set; } }
public sealed class LagRow { public double? P50 { get; set; } public double? P95 { get; set; } }
public sealed class StatusCountRow { public string Status { get; set; } = ""; public long Count { get; set; } }

public sealed record SummaryData(
    List<CategoryOutcomeRow> Counts, List<DeniedActorRow> DeniedActors, LagRow Lag,
    IntegrityCheckpoint? LatestCheckpoint, long ActiveHolds, List<StatusCountRow> Exports);

/// <summary>
/// Tenant summary. All aggregation runs in PostgreSQL; the six queries go to the server in ONE round trip (QueryMultiple), no N+1.
/// </summary>
public sealed class SummaryRepository(NpgsqlDataSource db)
{
    public async Task<SummaryData> GetAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct)
    {
        const string sql = """
            SELECT category, outcome, count(*) AS count
            FROM audit_events WHERE tenant_id = @tenantId AND occurred_at >= @from AND occurred_at < @to
            GROUP BY category, outcome ORDER BY category, outcome;

            SELECT actor_type, actor_id, count(*) AS count
            FROM audit_events WHERE tenant_id = @tenantId AND occurred_at >= @from AND occurred_at < @to AND outcome = 'Denied'
            GROUP BY actor_type, actor_id ORDER BY count(*) DESC, actor_id LIMIT 10;

            SELECT percentile_cont(0.5)  WITHIN GROUP (ORDER BY extract(epoch FROM received_at - occurred_at)::float8) AS p50,
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM received_at - occurred_at)::float8) AS p95
            FROM audit_events WHERE tenant_id = @tenantId AND occurred_at >= @from AND occurred_at < @to;

            SELECT id, tenant_id, sequence, hash, created_at FROM integrity_checkpoints
            WHERE tenant_id = @tenantId ORDER BY sequence DESC LIMIT 1;

            SELECT count(*) FROM legal_holds WHERE tenant_id = @tenantId AND released_at IS NULL;

            SELECT status, count(*) AS count FROM export_jobs WHERE tenant_id = @tenantId GROUP BY status ORDER BY status;
            """;

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var grid = await conn.QueryMultipleAsync(Database.Cmd(sql, new { tenantId, from, to }, ct: ct));
        return new SummaryData(
            (await grid.ReadAsync<CategoryOutcomeRow>()).ToList(),
            (await grid.ReadAsync<DeniedActorRow>()).ToList(),
            await grid.ReadSingleAsync<LagRow>(),
            await grid.ReadSingleOrDefaultAsync<IntegrityCheckpoint>(),
            await grid.ReadSingleAsync<long>(),
            (await grid.ReadAsync<StatusCountRow>()).ToList());
    }
}
