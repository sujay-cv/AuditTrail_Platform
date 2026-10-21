using AuditTrail.Api.Dtos;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;
using AuditTrail.Api.Repositories;

namespace AuditTrail.Api.Services;

public sealed class SummaryService(SummaryRepository summaries)
{
    public const int MaxRangeDays = 366;

    public async Task<SummaryDto> GetAsync(Guid tenantId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        if (from is null || to is null) throw ApiException.BadRequest("time_range_required", "Both 'from' and 'to' are required.");
        if (to <= from) throw ApiException.Field("to", "invalid_time_range", "'to' must be after 'from'.");
        if (to.Value - from.Value > TimeSpan.FromDays(MaxRangeDays))
            throw ApiException.Field("to", "time_range_too_large", $"The summary range may be at most {MaxRangeDays} days.");

        var d = await summaries.GetAsync(tenantId, from.Value.UtcDateTime, to.Value.UtcDateTime, ct);
        return new SummaryDto(
            from.Value.UtcDateTime, to.Value.UtcDateTime, d.Counts.Sum(c => c.Count),
            d.Counts.Select(c => new CategoryOutcomeCountDto(Enum.Parse<Category>(c.Category), Enum.Parse<Outcome>(c.Outcome), c.Count)).ToList(),
            d.DeniedActors.Select(a => new DeniedActorDto(Enum.Parse<ActorType>(a.ActorType), a.ActorId, a.Count)).ToList(),
            new IngestionLagDto(d.Lag.P50, d.Lag.P95),
            d.LatestCheckpoint is null ? null : Mappers.ToDto(d.LatestCheckpoint),
            d.ActiveHolds,
            d.Exports.Select(s => new ExportStatusCountDto(Enum.Parse<ExportStatus>(s.Status), s.Count)).ToList());
    }
}
