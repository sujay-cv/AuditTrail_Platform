using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using AuditTrail.Api.Models;

namespace AuditTrail.Api.Dtos;

// ---------- Ingestion (producers) ----------

public sealed record ActorInput(string? Type, string? Id, string? Ip, string? UserAgent);
public sealed record ResourceInput(string? Type, string? Id);

/// <summary>
/// One event as a producer sends it. Fields are nullable on purpose: we validate them ourselves so we can
/// return a precise per-field error instead of a generic "could not bind" error.
/// </summary>
public sealed record EventInput(
    string? EventId, string? OccurredAt, ActorInput? Actor, string? Action,
    ResourceInput? Resource, string? Outcome, string? Category, JsonElement? Metadata);

/// <summary>
/// Batch body. Items are kept as raw JSON so each one is validated separately:
/// one malformed event is rejected on its own and the valid ones are still stored.
/// </summary>
public sealed record BatchRequest(List<JsonElement>? Events);

public sealed record EventResultDto(
    int Index, string? EventId, IngestStatus Status, long? Sequence,
    string? Reason, string? Message, Dictionary<string, string[]>? Errors);

public sealed record BatchResponse(int Accepted, int Duplicates, int Conflicts, int Rejected, List<EventResultDto> Results);

// ---------- Reading events (auditors) ----------

public sealed record ActorDto(ActorType Type, string Id, string? Ip, string? UserAgent);
public sealed record ResourceDto(string Type, string Id);

/// <summary>A row in the search result list (no metadata, to keep large lists light).</summary>
public sealed record EventListItemDto(
    Guid Id, string EventId, long Sequence, DateTime OccurredAt, DateTime ReceivedAt,
    ActorType ActorType, string ActorId, string Action, string ResourceType, string ResourceId,
    Outcome Outcome, Category Category, Guid ProducerId, string ProducerName);

/// <summary>Full event, including its place in the hash chain.</summary>
public sealed record EventDto(
    Guid Id, string EventId, Guid ProducerId, string ProducerName, long Sequence,
    DateTime OccurredAt, DateTime ReceivedAt, ActorDto Actor, string Action, ResourceDto Resource,
    Outcome Outcome, Category Category, JsonElement Metadata, string[] RedactedKeys,
    int HashVersion, string PrevHash, string Hash);

/// <summary>Search query string. From/To are required; the range may not exceed the configured maximum (90 days).</summary>
public sealed class SearchQuery
{
    [FromQuery(Name = "from")]
    public DateTimeOffset? From { get; set; }
    [FromQuery(Name = "to")]
    public DateTimeOffset? To { get; set; }
    [FromQuery(Name = "actorId")]
    public string? ActorId { get; set; }
    [FromQuery(Name = "actionPrefix")]
    public string? ActionPrefix { get; set; }
    [FromQuery(Name = "resourceType")]
    public string? ResourceType { get; set; }
    [FromQuery(Name = "resourceId")]
    public string? ResourceId { get; set; }
    [FromQuery(Name = "outcome")]
    public Outcome? Outcome { get; set; }
    [FromQuery(Name = "category")]
    public Category? Category { get; set; }
    [FromQuery(Name = "producerId")]
    public Guid? ProducerId { get; set; }
    /// <summary>Opaque cursor from the previous page's nextCursor.</summary>
    [FromQuery(Name = "cursor")]
    public string? Cursor { get; set; }
    [FromQuery(Name = "limit")]
    public int? Limit { get; set; }
    /// <summary>false (default) = oldest first; true = newest first. Pass the same value with every page.</summary>
    [FromQuery(Name = "newestFirst")]
    public bool? NewestFirst { get; set; }
}

/// <summary>NextCursor is null on the last page.</summary>
public sealed record SearchResponse(List<EventListItemDto> Items, string? NextCursor);

public sealed record TenantDto(Guid Id, string Name);

public sealed record ProducerDto(Guid Id, string Name, string Status);
