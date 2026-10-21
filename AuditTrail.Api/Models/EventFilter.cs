namespace AuditTrail.Api.Models;

/// <summary>
/// Search / export filter. OccurredAt range is mandatory; everything else is optional.
/// Also stored as JSON inside export_jobs.filter.
/// </summary>
public sealed record EventFilter
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string? ActorId { get; init; }
    public string? ActionPrefix { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceId { get; init; }
    public Outcome? Outcome { get; init; }
    public Category? Category { get; init; }
    public Guid? ProducerId { get; init; }
}

/// <summary>Keyset position: the last row returned, in (OccurredAt, Sequence) order.</summary>
public sealed record EventCursor(DateTime OccurredAt, long Sequence);

/// <summary>
/// A validated, redacted, canonicalised event that is ready to be appended to the chain.
/// ReceivedAtOverride is used ONLY by the demo seeder to create history; the API never sets it (ReceivedAt = server clock).
/// </summary>
public sealed record NewEvent(
    string EventId, DateTime OccurredAt,
    string ActorType, string ActorId, string? ActorIp, string? ActorUserAgent,
    string Action, string ResourceType, string ResourceId,
    string Outcome, string Category, string MetadataJson, byte[] PayloadHash,
    DateTime? ReceivedAtOverride = null);

public enum AppendOutcome { Accepted, Duplicate, Conflict }

/// <summary>Result of appending one event: what happened and which sequence it has (new or original).</summary>
public sealed record AppendResult(AppendOutcome Outcome, long Sequence);
