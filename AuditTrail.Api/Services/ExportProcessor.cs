using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;
using AuditTrail.Api.Options;
using AuditTrail.Api.Repositories;

namespace AuditTrail.Api.Services;

/// <summary>
/// Writes one export job to an NDJSON file. Called by ExportWorker (and directly by tests).
///
/// Streaming: rows are read in chunks of Export:ChunkSize with keyset pagination and written straight to the file,
/// so memory use is one chunk no matter how many rows the export has.
///
/// Resumable: after every chunk the file is flushed to disk, THEN resume_cursor / row_count / file_bytes are saved
/// (which also renews the lease). If the worker dies, the next worker truncates the file back to file_bytes (dropping
/// anything written after the last saved progress) and continues after resume_cursor: no duplicate and no missing rows.
/// </summary>
public sealed class ExportProcessor(
    ExportRepository exports, EventRepository events, IOptions<ExportOptions> options, AuditMetrics metrics, ILogger<ExportProcessor> logger)
{
    private readonly ExportOptions _opt = options.Value;
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    /// <summary>Claims and processes one job. Returns false when there was nothing to do.</summary>
    public async Task<bool> RunOnceAsync(string workerId, CancellationToken ct, Action? onChunkWritten = null)
    {
        await exports.RecoverExpiredLeasesAsync(_opt.MaxAttempts, ct);

        var job = await exports.ClaimNextAsync(workerId, _opt.LeaseSeconds, ct);
        if (job is null) return false;

        using (logger.BeginScope("JobId:{JobId} TenantId:{TenantId}", job.Id, job.TenantId))
        {
            logger.LogInformation("Claimed export (attempt {Attempt}, resuming at row {RowCount})", job.Attempts, job.RowCount);
            try
            {
                await ProcessAsync(job, workerId, onChunkWritten, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutting down: hand the job back so another instance can resume it right away.
                await exports.ReleaseAsync(job.Id, workerId, CancellationToken.None);
                logger.LogInformation("Shutdown: export released for resumption at row {RowCount}", job.RowCount);
                throw;
            }
            // Any other exception: leave the job Running. Its lease expires, it returns to Queued and is retried
            // (up to Export:MaxAttempts, then Failed). That is the same path as a crashed worker.
        }
        return true;
    }

    private async Task ProcessAsync(ExportJob job, string workerId, Action? onChunkWritten, CancellationToken ct)
    {
        var directory = Path.Combine(_opt.Directory, job.TenantId.ToString());
        Directory.CreateDirectory(directory);
        var finalPath = Path.GetFullPath(Path.Combine(directory, $"{job.Id}.ndjson"));
        var partPath = finalPath + ".part";

        // Crash between "rename .part -> final" and "mark Completed": move it back and carry on.
        if (!File.Exists(partPath) && File.Exists(finalPath)) File.Move(finalPath, partPath);

        var filter = JsonSerializer.Deserialize<EventFilter>(job.Filter, JsonSerializerOptions.Web)!;
        var cursor = job.ResumeCursor is null ? null : JsonSerializer.Deserialize<EventCursor>(job.ResumeCursor, JsonSerializerOptions.Web);
        var rowCount = job.RowCount;
        var lostOwnership = false;

        await using (var file = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            file.SetLength(job.FileBytes);          // drop bytes written after the last saved progress
            file.Seek(0, SeekOrigin.End);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = await events.ReadChunkAsync(job.TenantId, filter, cursor, job.SnapshotSequence, _opt.ChunkSize, ct);
                if (chunk.Count == 0) break;

                foreach (var e in chunk)
                {
                    await file.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(ToLine(e), JsonSerializerOptions.Web), ct);
                    await file.WriteAsync(NewLine, ct);
                }
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);      // durable on disk BEFORE we record progress

                rowCount += chunk.Count;
                cursor = new EventCursor(chunk[^1].OccurredAt, chunk[^1].Sequence);
                metrics.ExportRows(chunk.Count);
                var stillOwned = await exports.SaveProgressAsync(job.Id, workerId,
                    JsonSerializer.Serialize(cursor, JsonSerializerOptions.Web), rowCount, file.Position, _opt.LeaseSeconds, ct);
                if (!stillOwned)
                {
                    lostOwnership = true;
                    break;
                }
                onChunkWritten?.Invoke();

                if (_opt.DelayPerChunkMs > 0) await Task.Delay(_opt.DelayPerChunkMs, ct);
                if (chunk.Count < _opt.ChunkSize) break;
            }
        }

        if (lostOwnership)
        {
            await StopBecauseNotOwnedAsync(job.Id, partPath, ct);
            return;
        }

        var checksum = await Sha256FileAsync(partPath, ct);
        var length = new FileInfo(partPath).Length;
        File.Move(partPath, finalPath, overwrite: true);

        if (await exports.CompleteAsync(job.Id, workerId, finalPath, checksum, rowCount, length, ct))
            logger.LogInformation("Export completed: {RowCount} rows, sha256 {Checksum}", rowCount, checksum);
        else
            await StopBecauseNotOwnedAsync(job.Id, finalPath, ct);
    }

    /// <summary>The job was cancelled (delete the partial file) or another worker took over after our lease expired (leave it).</summary>
    private async Task StopBecauseNotOwnedAsync(Guid jobId, string path, CancellationToken ct)
    {
        var status = await exports.GetStatusAsync(jobId, ct);
        if (status == nameof(ExportStatus.Cancelled))
        {
            File.Delete(path);
            logger.LogInformation("Export was cancelled; partial file deleted");
        }
        else logger.LogWarning("Lost the lease on the export (now {Status}); stopping", status);
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    private static object ToLine(AuditEvent e)
    {
        using var metadata = JsonDocument.Parse(e.Metadata);
        return new
        {
            e.Id, e.EventId, e.Sequence, e.OccurredAt, e.ReceivedAt, e.ProducerId,
            Actor = new { Type = e.ActorType, Id = e.ActorId, Ip = e.ActorIp, UserAgent = e.ActorUserAgent },
            e.Action,
            Resource = new { Type = e.ResourceType, Id = e.ResourceId },
            e.Outcome, e.Category,
            Metadata = metadata.RootElement.Clone(),
            PrevHash = CanonicalSerializer.Hex(e.PrevHash),
            Hash = CanonicalSerializer.Hex(e.Hash),
        };
    }
}
