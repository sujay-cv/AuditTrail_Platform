using Microsoft.Extensions.Options;
using AuditTrail.Api.Dtos;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;
using AuditTrail.Api.Options;
using AuditTrail.Api.Repositories;

namespace AuditTrail.Api.Services;

/// <summary>Search and single-event reads. Also owns the filter validation that exports reuse.</summary>
public sealed class EventQueryService(EventRepository events, CursorCodec cursors, IOptions<SearchOptions> options)
{
    private readonly SearchOptions _opt = options.Value;

    /// <summary>
    /// Bounded search: an OccurredAt range is REQUIRED and may be at most 90 days, so no query can scan a tenant's whole history.
    /// Sorted by (OccurredAt, Sequence), oldest first by default or newest first with newestFirst=true.
    /// OccurredAt is what an auditor thinks in; Sequence breaks ties deterministically.
    /// </summary>
    public async Task<SearchResponse> SearchAsync(Guid tenantId, SearchQuery q, CancellationToken ct)
    {
        var filter = ValidateFilter(q.From, q.To, q.ActorId, q.ActionPrefix, q.ResourceType, q.ResourceId, q.Outcome, q.Category, q.ProducerId);
        var newestFirst = q.NewestFirst ?? false;

        var limit = q.Limit ?? _opt.DefaultPageSize;
        if (limit < 1 || limit > _opt.MaxPageSize)
            throw ApiException.Field("limit", "invalid_page_size", $"limit must be between 1 and {_opt.MaxPageSize}.");

        EventCursor? after = null;
        long maxSequence;
        if (string.IsNullOrEmpty(q.Cursor))
            maxSequence = await events.GetHeadSequenceAsync(tenantId, ct);  // first page: pin the snapshot
        else
            (after, maxSequence) = cursors.Decode(q.Cursor, filter, newestFirst);

        // Ask for one extra row to know whether there is a next page.
        var rows = await events.SearchAsync(tenantId, filter, after, maxSequence, limit + 1, newestFirst, ct);
        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            next = cursors.Encode(new EventCursor(last.OccurredAt, last.Sequence), maxSequence, filter, newestFirst);
        }
        return new SearchResponse(rows.Select(Mappers.ToListItem).ToList(), next);
    }

    public async Task<EventDto> GetAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        var found = await events.GetByIdAsync(tenantId, id, ct) ?? throw ApiException.NotFound("Event");
        return Mappers.ToDto(found.Event, found.ProducerName);
    }

    public EventFilter ValidateFilter(
        DateTimeOffset? from, DateTimeOffset? to, string? actorId, string? actionPrefix, string? resourceType, string? resourceId,
        Outcome? outcome, Category? category, Guid? producerId)
    {
        if (from is null || to is null)
            throw ApiException.BadRequest("time_range_required", "Both 'from' and 'to' (OccurredAt range, UTC) are required.",
                new Dictionary<string, string[]> { [from is null ? "from" : "to"] = ["Required."] });
        if (to <= from)
            throw ApiException.Field("to", "invalid_time_range", "'to' must be after 'from'.");
        if (to.Value - from.Value > TimeSpan.FromDays(_opt.MaxRangeDays))
            throw ApiException.Field("to", "time_range_too_large", $"The time range may be at most {_opt.MaxRangeDays} days.");

        return new EventFilter
        {
            From = from.Value.UtcDateTime,
            To = to.Value.UtcDateTime,
            ActorId = Clean(actorId, "actorId"),
            ActionPrefix = Clean(actionPrefix, "actionPrefix"),
            ResourceType = Clean(resourceType, "resourceType"),
            ResourceId = Clean(resourceId, "resourceId"),
            Outcome = outcome,
            Category = category,
            ProducerId = producerId,
        };
    }

    private static string? Clean(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= 256 ? value : throw ApiException.Field(field, "too_long", $"{field} must be at most 256 characters.");
    }
}
