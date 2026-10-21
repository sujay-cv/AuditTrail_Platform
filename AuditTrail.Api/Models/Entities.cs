namespace AuditTrail.Api.Models;

// Entity classes = one row of a table. Dapper fills them (snake_case column -> PascalCase property).
// Enum-like columns are kept as strings here; the DTOs expose the typed enums.

public sealed class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string[] RedactionKeys { get; set; } = [];
}

public sealed class Producer
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string ApiKeyHash { get; set; } = "";
    public string Status { get; set; } = "";  // Active | Revoked
}

/// <summary>One stored audit event. Never updated after insert.</summary>
public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProducerId { get; set; }
    public string EventId { get; set; } = "";
    public long Sequence { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string ActorType { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string? ActorIp { get; set; }
    public string? ActorUserAgent { get; set; }
    public string Action { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Category { get; set; } = "";
    public string Metadata { get; set; } = "{}";   // jsonb, read as text
    public byte[] PayloadHash { get; set; } = [];
    public short HashVersion { get; set; }
    public byte[] PrevHash { get; set; } = [];
    public byte[] Hash { get; set; } = [];
}

/// <summary>Per-tenant head of the hash chain. Locked with FOR UPDATE while appending.</summary>
public sealed class ChainHead
{
    public Guid TenantId { get; set; }
    public long LastSequence { get; set; }
    public byte[] LastHash { get; set; } = [];
    public DateTime LastReceivedAt { get; set; }
}

public sealed class IntegrityCheckpoint
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public long Sequence { get; set; }
    public byte[] Hash { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}

public sealed class RetentionPolicy
{
    public Guid TenantId { get; set; }
    public string Category { get; set; } = "";
    public int RetentionDays { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class LegalHold
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string ScopeType { get; set; } = "";
    public string ScopeValue { get; set; } = "";
    public string Reason { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public string? ReleasedBy { get; set; }
}

public sealed class ExportJob
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Filter { get; set; } = "{}";      // jsonb (EventFilter)
    public string Format { get; set; } = "";
    public string Status { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public long SnapshotSequence { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public int Attempts { get; set; }
    public string? ResumeCursor { get; set; }       // jsonb (EventCursor)
    public long RowCount { get; set; }
    public long FileBytes { get; set; }
    public string? FileLocation { get; set; }
    public string? Checksum { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
