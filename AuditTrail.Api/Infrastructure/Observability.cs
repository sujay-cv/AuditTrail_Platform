using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace AuditTrail.Api.Infrastructure;

/// <summary>
/// Custom metrics through System.Diagnostics.Metrics. Watch them live with:
///   dotnet-counters monitor -n AuditTrail.Api --counters AuditTrail
/// </summary>
public sealed class AuditMetrics
{
    public const string MeterName = "AuditTrail";

    private readonly Counter<long> _accepted, _duplicates, _conflicts, _rejected, _exportRows, _retentionDeleted, _verificationFailures;
    private readonly Histogram<double> _ingestDuration, _chainLockWait;

    public AuditMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _accepted = meter.CreateCounter<long>("audit.events.accepted");
        _duplicates = meter.CreateCounter<long>("audit.events.duplicates");
        _conflicts = meter.CreateCounter<long>("audit.events.conflicts");
        _rejected = meter.CreateCounter<long>("audit.events.rejected", description: "Tagged with reason");
        _ingestDuration = meter.CreateHistogram<double>("audit.ingest.duration", "ms");
        _chainLockWait = meter.CreateHistogram<double>("audit.chain.lock_wait", "ms", "Time waiting for the per-tenant chain lock");
        _exportRows = meter.CreateCounter<long>("audit.export.rows", description: "Rows written to export files (rate = rows/second)");
        _retentionDeleted = meter.CreateCounter<long>("audit.retention.deleted");
        _verificationFailures = meter.CreateCounter<long>("audit.verification.failures");
    }

    public void Accepted(long n) => _accepted.Add(n);
    public void Duplicates(long n) => _duplicates.Add(n);
    public void Conflicts(long n) => _conflicts.Add(n);
    public void Rejected(string reason) => _rejected.Add(1, new KeyValuePair<string, object?>("reason", reason));
    public void IngestDuration(TimeSpan t) => _ingestDuration.Record(t.TotalMilliseconds);
    public void ChainLockWait(TimeSpan t) => _chainLockWait.Record(t.TotalMilliseconds);
    public void ExportRows(long n) => _exportRows.Add(n);
    public void RetentionDeleted(long n) => _retentionDeleted.Add(n);
    public void VerificationFailure() => _verificationFailures.Add(1);
}

/// <summary>Background workers "beat" every loop. Readiness fails if a worker stops beating (stuck or crashed).</summary>
public sealed class WorkerHeartbeat
{
    private readonly ConcurrentDictionary<string, (DateTime LastBeat, TimeSpan MaxSilence)> _workers = new();

    public void Beat(string worker, TimeSpan maxSilence) => _workers[worker] = (DateTime.UtcNow, maxSilence);

    public void Remove(string worker) => _workers.TryRemove(worker, out _);

    public IEnumerable<string> StaleWorkers()
        => _workers.Where(w => DateTime.UtcNow - w.Value.LastBeat > w.Value.MaxSilence).Select(w => w.Key);
}

public sealed class WorkerHeartbeatHealthCheck(WorkerHeartbeat heartbeat) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var stale = heartbeat.StaleWorkers().ToList();
        return Task.FromResult(stale.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Stale workers: " + string.Join(", ", stale)));
    }
}

public sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await using var cmd = dataSource.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unreachable");
        }
    }
}
