using Microsoft.Extensions.Options;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Options;
using AuditTrail.Api.Repositories;
using AuditTrail.Api.Services;

namespace AuditTrail.Api.Workers;

/// <summary>
/// Every Retention:IntervalSeconds, for each tenant:
///   1. Verify the chain from the latest checkpoint to the head and write a new checkpoint.
///   2. Only if the chain is intact: purge expired events in bounded batches, skipping anything under an active legal hold.
///      (If the chain is broken we do NOT delete anything: purging could destroy evidence of the tampering.)
/// Several instances may run this at once: purge uses SKIP LOCKED and checkpoints are insert-if-absent.
/// </summary>
public sealed class RetentionWorker(
    TenantRepository tenants, IntegrityService integrity, RetentionService retention, WorkerHeartbeat heartbeat,
    IOptions<RetentionOptions> options, ILogger<RetentionWorker> logger) : BackgroundService
{
    public const string Name = "retention-worker";
    private const string ActorId = "system:retention-worker";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.IntervalSeconds);
        var maxSilence = interval * 2 + TimeSpan.FromMinutes(5);
        var failures = 0;

        // First run after one interval, so a fresh demo database is not purged before you can look at it.
        heartbeat.Beat(Name, maxSilence);
        if (!await Backoff.SleepAsync(interval, stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            heartbeat.Beat(Name, maxSilence);
            try
            {
                foreach (var tenant in await tenants.ListAsync(stoppingToken))
                {
                    using var _ = logger.BeginScope("TenantId:{TenantId}", tenant.Id);

                    if (!await integrity.CreateCheckpointAsync(tenant.Id, stoppingToken))
                    {
                        logger.LogError("Hash chain verification FAILED; no checkpoint written and retention skipped for this tenant");
                        continue;
                    }
                    await retention.PurgeTenantAsync(tenant.Id, ActorId, stoppingToken);
                    heartbeat.Beat(Name, maxSilence);
                }
                failures = 0;
                if (!await Backoff.SleepAsync(interval, stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                var delay = Backoff.Delay(failures);
                logger.LogWarning("Retention worker error ({ErrorType}); retrying in {Delay:0.0}s (failure #{Failures})", ex.GetType().Name, delay.TotalSeconds, failures);
                if (!await Backoff.SleepAsync(delay, stoppingToken)) break;
            }
        }
        heartbeat.Remove(Name);
    }
}
