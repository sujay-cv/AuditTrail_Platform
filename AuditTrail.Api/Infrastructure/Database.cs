using System.Reflection;
using Dapper;
using Npgsql;

namespace AuditTrail.Api.Infrastructure;

public static class Database
{
    /// <summary>
    /// One pooled data source for the whole app (thread-safe, registered as a singleton).
    /// Pool size and timeouts are set deliberately in the connection string (appsettings.json):
    ///  - Maximum Pool Size=50 : per API instance. Keep (instances x 50) below PostgreSQL max_connections (100 by default).
    ///  - Timeout=5            : seconds to get a connection. Fail fast with 503 when PostgreSQL is down instead of hanging.
    ///  - Command Timeout=30   : no statement may run longer than 30s (searches are bounded to 90 days anyway).
    ///  - Keepalive=30         : detect dead connections after a database restart.
    /// </summary>
    public static NpgsqlDataSource Create(string connectionString) => NpgsqlDataSource.Create(connectionString);

    /// <summary>Dapper: map snake_case columns (tenant_id) to PascalCase properties (TenantId).</summary>
    public static void ConfigureDapper() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public static CommandDefinition Cmd(string sql, object? args = null, NpgsqlTransaction? tx = null, CancellationToken ct = default)
        => new(sql, args, tx, cancellationToken: ct);
}

/// <summary>
/// Applies Migrations/*.sql (embedded in the dll) in name order, each exactly once, recorded in schema_migrations.
/// A PostgreSQL advisory lock makes it safe when several API instances start at the same time.
/// </summary>
public sealed class Migrator(NpgsqlDataSource dataSource, ILogger<Migrator> logger)
{
    private const long LockKey = 727_274_001;

    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var conn = await OpenWithRetryAsync(ct);
        await conn.ExecuteAsync(Database.Cmd("SELECT pg_advisory_lock(@LockKey)", new { LockKey }, ct: ct));
        try
        {
            await conn.ExecuteAsync(Database.Cmd(
                "CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", ct: ct));
            var applied = (await conn.QueryAsync<string>(Database.Cmd("SELECT version FROM schema_migrations", ct: ct))).ToHashSet();

            var assembly = Assembly.GetExecutingAssembly();
            foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql")).Order(StringComparer.Ordinal))
            {
                var version = resource.Split('.')[^2]; // "AuditTrail.Api.Migrations.001_schema.sql" -> "001_schema"
                if (applied.Contains(version)) continue;

                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
                var sql = await reader.ReadToEndAsync(ct);

                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(Database.Cmd(sql, tx: tx, ct: ct));
                await conn.ExecuteAsync(Database.Cmd("INSERT INTO schema_migrations (version) VALUES (@version)", new { version }, tx, ct));
                await tx.CommitAsync(ct);
                logger.LogInformation("Applied migration {Version}", version);
            }
        }
        finally
        {
            await conn.ExecuteAsync(Database.Cmd("SELECT pg_advisory_unlock(@LockKey)", new { LockKey }));
        }
    }

    private async Task<NpgsqlConnection> OpenWithRetryAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await dataSource.OpenConnectionAsync(ct); }
            catch (NpgsqlException) when (attempt < 30)
            {
                logger.LogWarning("PostgreSQL is not reachable yet (attempt {Attempt}), retrying in 2s", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }
}
