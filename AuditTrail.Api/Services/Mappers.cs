using System.Text.Json;
using AuditTrail.Api.Dtos;
using AuditTrail.Api.Models;
using AuditTrail.Api.Repositories;

namespace AuditTrail.Api.Services;

/// <summary>Entity -> DTO conversions in one place.</summary>
public static class Mappers
{
    public static EventListItemDto ToListItem(EventListRow r) => new(
        r.Id, r.EventId, r.Sequence, r.OccurredAt, r.ReceivedAt,
        Enum.Parse<ActorType>(r.ActorType), r.ActorId, r.Action, r.ResourceType, r.ResourceId,
        Enum.Parse<Outcome>(r.Outcome), Enum.Parse<Category>(r.Category), r.ProducerId, r.ProducerName);

    public static EventDto ToDto(AuditEvent e, string producerName)
    {
        using var doc = JsonDocument.Parse(e.Metadata);
        var metadata = doc.RootElement.Clone();
        return new EventDto(
            e.Id, e.EventId, e.ProducerId, producerName, e.Sequence, e.OccurredAt, e.ReceivedAt,
            new ActorDto(Enum.Parse<ActorType>(e.ActorType), e.ActorId, e.ActorIp, e.ActorUserAgent),
            e.Action, new ResourceDto(e.ResourceType, e.ResourceId),
            Enum.Parse<Outcome>(e.Outcome), Enum.Parse<Category>(e.Category),
            metadata, RedactedPaths(metadata, "").ToArray(),
            e.HashVersion, CanonicalSerializer.Hex(e.PrevHash), CanonicalSerializer.Hex(e.Hash));
    }

    /// <summary>JSON paths (e.g. "card.number") whose value was masked at ingestion, so the UI can mark them clearly.</summary>
    private static IEnumerable<string> RedactedPaths(JsonElement e, string prefix)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                var path = prefix.Length == 0 ? p.Name : prefix + "." + p.Name;
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == CanonicalSerializer.RedactedValue) yield return path;
                else foreach (var inner in RedactedPaths(p.Value, path)) yield return inner;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in e.EnumerateArray())
                foreach (var inner in RedactedPaths(item, $"{prefix}[{i++}]")) yield return inner;
        }
    }

    public static EventFilterDto ToDto(EventFilter f) => new(
        new DateTimeOffset(f.From), new DateTimeOffset(f.To), f.ActorId, f.ActionPrefix, f.ResourceType, f.ResourceId, f.Outcome, f.Category, f.ProducerId);

    public static ExportJobDto ToDto(ExportJob j)
    {
        var filter = JsonSerializer.Deserialize<EventFilter>(j.Filter, JsonSerializerOptions.Web)!;
        var cursor = j.ResumeCursor is null ? null : JsonSerializer.Deserialize<EventCursor>(j.ResumeCursor, JsonSerializerOptions.Web);
        return new ExportJobDto(
            j.Id, Enum.Parse<ExportStatus>(j.Status), Enum.Parse<ExportFormat>(j.Format), ToDto(filter), j.RequestedBy,
            j.Attempts, j.RowCount, j.SnapshotSequence,
            cursor is null ? null : $"{CanonicalSerializer.FormatTimestamp(cursor.OccurredAt)} / seq {cursor.Sequence}",
            j.Checksum, j.Error, j.CreatedAt, j.CompletedAt, j.LeaseExpiresAt);
    }

    public static LegalHoldDto ToDto(LegalHold h) => new(
        h.Id, Enum.Parse<HoldScopeType>(h.ScopeType), h.ScopeValue, h.Reason, h.CreatedBy, h.CreatedAt, h.ReleasedAt, h.ReleasedBy, h.ReleasedAt is null);

    public static RetentionPolicyDto ToDto(RetentionPolicy p) => new(Enum.Parse<Category>(p.Category), p.RetentionDays, p.UpdatedAt);

    public static CheckpointDto ToDto(IntegrityCheckpoint c) => new(c.Sequence, CanonicalSerializer.Hex(c.Hash), c.CreatedAt);
}
