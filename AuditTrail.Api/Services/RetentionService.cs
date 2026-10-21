using Npgsql;
using AuditTrail.Api.Dtos;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;
using AuditTrail.Api.Options;
using AuditTrail.Api.Repositories;
using Microsoft.Extensions.Options;

namespace AuditTrail.Api.Services;

/// <summary>Retention policies, legal holds, and running the purge. Every change is self-audited in the same transaction.</summary>
public sealed class RetentionService(
    NpgsqlDataSource db, RetentionRepository retention, SelfAuditService selfAudit, AuditMetrics metrics,
    IOptions<RetentionOptions> options, ILogger<RetentionService> logger)
{
    public async Task<List<RetentionPolicyDto>> ListPoliciesAsync(Guid tenantId, CancellationToken ct)
        => (await retention.ListPoliciesAsync(tenantId, ct)).Select(Mappers.ToDto).ToList();

    public async Task<RetentionPolicyDto> UpdatePolicyAsync(Guid tenantId, string userId, Category category, int retentionDays, CancellationToken ct)
    {
        if (retentionDays is < 1 or > 36500)
            throw ApiException.Field("retentionDays", "invalid_retention_days", "retentionDays must be between 1 and 36500.");

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var policy = await retention.UpsertPolicyAsync(conn, tx, tenantId, category.ToString(), retentionDays, ct);
        await selfAudit.RecordAsync(conn, tx, tenantId, userId, "retention.policy.updated", "RetentionPolicy", category.ToString(),
            Category.Configuration, new { category = category.ToString(), retentionDays }, ct);
        await tx.CommitAsync(ct);
        return Mappers.ToDto(policy);
    }

    public async Task<List<LegalHoldDto>> ListHoldsAsync(Guid tenantId, CancellationToken ct)
        => (await retention.ListHoldsAsync(tenantId, ct)).Select(Mappers.ToDto).ToList();

    public async Task<LegalHoldDto> CreateHoldAsync(Guid tenantId, string userId, CreateLegalHoldRequest req, CancellationToken ct)
    {
        if (req.ScopeType is null) throw ApiException.Field("scopeType", "required", "scopeType is required (Actor, Resource or Category).");
        var value = req.ScopeValue?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 256) throw ApiException.Field("scopeValue", "required", "scopeValue is required (max 256 characters).");
        if (req.ScopeType == HoldScopeType.Category && !Enum.TryParse<Category>(value, out _))
            throw ApiException.Field("scopeValue", "invalid_value", "For a Category hold, scopeValue must be a category name.");
        if (req.ScopeType == HoldScopeType.Resource && !value.Contains('/'))
            throw ApiException.Field("scopeValue", "invalid_format", "For a Resource hold, scopeValue must be 'Type/Id', e.g. Invoice/INV-20931.");
        var reason = req.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 1000) throw ApiException.Field("reason", "required", "A reason is required (max 1000 characters).");

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var hold = await retention.InsertHoldAsync(conn, tx, new LegalHold
        {
            Id = Guid.CreateVersion7(), TenantId = tenantId, ScopeType = req.ScopeType.Value.ToString(),
            ScopeValue = value, Reason = reason, CreatedBy = userId,
        }, ct);
        await selfAudit.RecordAsync(conn, tx, tenantId, userId, "legal_hold.created", "LegalHold", hold.Id.ToString(), Category.Configuration,
            new { scopeType = hold.ScopeType, scopeValue = hold.ScopeValue, reason }, ct);
        await tx.CommitAsync(ct);
        return Mappers.ToDto(hold);
    }

    public async Task<LegalHoldDto> ReleaseHoldAsync(Guid tenantId, string userId, Guid id, ReleaseLegalHoldRequest req, CancellationToken ct)
    {
        var reason = req.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 1000) throw ApiException.Field("reason", "required", "A reason is required (max 1000 characters).");

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var hold = await retention.ReleaseHoldAsync(conn, tx, tenantId, id, userId, ct);
        if (hold is null)
        {
            _ = await retention.GetHoldAsync(tenantId, id, ct) ?? throw ApiException.NotFound("Legal hold");
            throw ApiException.Conflict("legal_hold_already_released", "This legal hold is already released.");
        }
        await selfAudit.RecordAsync(conn, tx, tenantId, userId, "legal_hold.released", "LegalHold", id.ToString(), Category.Configuration,
            new { reason }, ct);
        await tx.CommitAsync(ct);
        return Mappers.ToDto(hold);
    }

    /// <summary>
    /// Deletes expired, un-held events of one tenant in bounded batches (each batch is its own short transaction).
    /// Stops when a batch comes back smaller than the batch size, or on cancellation. Records one self-audit event with the total.
    /// </summary>
    public async Task<int> PurgeTenantAsync(Guid tenantId, string actorId, CancellationToken ct)
    {
        var batchSize = options.Value.BatchSize;
        var total = 0;
        int deleted;
        do
        {
            deleted = await retention.PurgeBatchAsync(tenantId, batchSize, ct);
            total += deleted;
            metrics.RetentionDeleted(deleted);
        } while (deleted == batchSize && !ct.IsCancellationRequested);

        if (total > 0)
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await selfAudit.RecordAsync(conn, tx, tenantId, actorId, "retention.purge.completed", "Tenant", tenantId.ToString(),
                Category.Configuration, new { deleted = total }, ct);
            await tx.CommitAsync(ct);
            logger.LogInformation("Retention purged {Deleted} events for tenant {TenantId}", total, tenantId);
        }
        return total;
    }
}
