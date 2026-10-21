using Microsoft.Extensions.Options;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Options;
using AuditTrail.Api.Repositories;
using AuditTrail.Api.Services;

namespace AuditTrail.Api.Workers;

/// <summary>
/// Export processor loop. Safe to run on many instances at once: jobs are claimed with FOR UPDATE SKIP LOCKED and a lease.
///  - Idle: poll every Export:PollSeconds (no tight loop) and expire old completed files.
///  - Database down: exponential backoff, keep beating the heartbeat, recover automatically.
///  - Shutdown: the stopping token cancels the current chunk; the job is handed back (Running -> Queued) for resumption.
/// </summary>
public sealed class ExportWorker(
    ExportProcessor processor, ExportRepository exports, WorkerHeartbeat heartbeat,
    IOptions<ExportOptions> options, ILogger<ExportWorker> logger) : BackgroundService
{
    public const string Name = "export-worker";
    private readonly ExportOptions _opt = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerId = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..6]}";
        var maxSilence = TimeSpan.FromSeconds(Math.Max(120, _opt.LeaseSeconds * 4));
        var failures = 0;
        var nextExpiry = DateTime.MinValue;
        logger.LogInformation("Export worker {WorkerId} started", workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            heartbeat.Beat(Name, maxSilence);
            try
            {
                var didWork = await processor.RunOnceAsync(workerId, stoppingToken, () => heartbeat.Beat(Name, maxSilence));
                failures = 0;
                if (didWork) continue;   // there may be more queued jobs

                if (DateTime.UtcNow >= nextExpiry)
                {
                    await ExpireOldFilesAsync(stoppingToken);
                    nextExpiry = DateTime.UtcNow.AddMinutes(5);
                }
                if (!await Backoff.SleepAsync(TimeSpan.FromSeconds(_opt.PollSeconds), stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                var delay = Backoff.Delay(failures);
                logger.LogWarning("Export worker error ({ErrorType}); retrying in {Delay:0.0}s (failure #{Failures})", ex.GetType().Name, delay.TotalSeconds, failures);
                if (!await Backoff.SleepAsync(delay, stoppingToken)) break;
            }
        }

        heartbeat.Remove(Name);
        logger.LogInformation("Export worker {WorkerId} stopped", workerId);
    }

    private async Task ExpireOldFilesAsync(CancellationToken ct)
    {
        foreach (var file in await exports.ExpireCompletedAsync(_opt.FileRetentionHours, ct))
        {
            try { File.Delete(file); }
            catch (IOException ex) { logger.LogWarning("Could not delete expired export file: {Error}", ex.Message); }
        }
    }
}
