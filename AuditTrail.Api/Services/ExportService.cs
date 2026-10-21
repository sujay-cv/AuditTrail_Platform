using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using AuditTrail.Api.Dtos;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;
using AuditTrail.Api.Repositories;

namespace AuditTrail.Api.Services;

/// <summary>
/// API side of exports: create (idempotent), read, cancel, download. The actual file writing is done by ExportProcessor.
///
/// Why export creation needs an Idempotency-Key (ingestion does not): ingestion already has a natural key (EventId).
/// "Create export" does not, so a double-click or a network retry would otherwise start two jobs.
/// </summary>
public sealed class ExportService(
    NpgsqlDataSource db, ExportRepository exports, EventRepository events, EventQueryService queries, SelfAuditService selfAudit)
{
    public async Task<(ExportJobDto Job, bool Created)> CreateAsync(
        Guid tenantId, string userId, string? idempotencyKey, CreateExportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw ApiException.BadRequest("idempotency_key_required", "The Idempotency-Key header is required.");
        if (idempotencyKey.Length > 100)
            throw ApiException.BadRequest("idempotency_key_invalid", "Idempotency-Key must be at most 100 characters.");

        var f = request.Filter ?? throw ApiException.Field("filter", "required", "filter is required.");
        var filter = queries.ValidateFilter(f.From, f.To, f.ActorId, f.ActionPrefix, f.ResourceType, f.ResourceId, f.Outcome, f.Category, f.ProducerId);
        var filterJson = JsonSerializer.Serialize(filter, JsonSerializerOptions.Web);
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { filterJson, request.Format })));

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var job = new ExportJob
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Filter = filterJson,
            Format = request.Format.ToString(),
            RequestedBy = userId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            // The export contains events up to the current head only, so a resumed or retried run produces the same file.
            SnapshotSequence = await events.GetHeadSequenceAsync(conn, tx, tenantId, ct),
        };

        var (saved, created) = await exports.InsertOrGetAsync(conn, tx, job, ct);
        if (!created && saved.RequestHash != requestHash)
            throw ApiException.Conflict("idempotency_key_reused", "This Idempotency-Key was already used for a different export request.");

        if (created)
            await selfAudit.RecordAsync(conn, tx, tenantId, userId, "export.requested", "ExportJob", saved.Id.ToString(), Category.Access,
                new { filter = Mappers.ToDto(filter), format = request.Format.ToString() }, ct);

        await tx.CommitAsync(ct);
        return (Mappers.ToDto(saved), created);
    }

    public async Task<ExportJobDto> GetAsync(Guid tenantId, Guid id, CancellationToken ct)
        => Mappers.ToDto(await exports.GetAsync(tenantId, id, ct) ?? throw ApiException.NotFound("Export job"));

    public async Task<List<ExportJobDto>> ListAsync(Guid tenantId, CancellationToken ct)
        => (await exports.ListAsync(tenantId, 50, ct)).Select(Mappers.ToDto).ToList();

    /// <summary>Queued/Running -> Cancelled. Any terminal job -> 409 with its current status.</summary>
    public async Task<ExportJobDto> CancelAsync(Guid tenantId, string userId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var cancelled = await exports.CancelAsync(conn, tx, tenantId, id, ct);
        if (cancelled is null)
        {
            var current = await exports.GetAsync(tenantId, id, ct) ?? throw ApiException.NotFound("Export job");
            throw ApiException.Conflict("export_not_cancellable", $"The export is {current.Status} and can no longer be cancelled.",
                new Dictionary<string, object?> { ["currentStatus"] = current.Status });
        }

        await selfAudit.RecordAsync(conn, tx, tenantId, userId, "export.cancelled", "ExportJob", id.ToString(), Category.Access, new { }, ct);
        await tx.CommitAsync(ct);
        return Mappers.ToDto(cancelled);
    }

    /// <summary>Returns the file path and checksum of a Completed export.</summary>
    public async Task<(string Path, string Checksum, string FileName)> GetDownloadAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        var job = await exports.GetAsync(tenantId, id, ct) ?? throw ApiException.NotFound("Export job");
        if (job.Status != nameof(ExportStatus.Completed) || job.FileLocation is null || job.Checksum is null)
            throw ApiException.Conflict("export_not_ready", $"The export is {job.Status}; only Completed exports can be downloaded.",
                new Dictionary<string, object?> { ["currentStatus"] = job.Status });
        if (!File.Exists(job.FileLocation))
            throw ApiException.NotFound("Export file");
        return (job.FileLocation, job.Checksum, $"audit-export-{job.Id}.ndjson");
    }
}
