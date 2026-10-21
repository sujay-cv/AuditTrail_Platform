using AuditTrail.Api.Models;

namespace AuditTrail.Api.Dtos;

// ---------- Integrity ----------

public sealed record VerifyRequest(long? FromSequence, long? ToSequence);

public sealed record CheckpointDto(long Sequence, string Hash, DateTime CreatedAt);

/// <summary>
/// Result of walking the chain. Ok = false is a normal result (tampering found), not an error.
/// Verification starts at CheckpointUsed (or the beginning of the chain when null).
/// </summary>
public sealed record VerifyResultDto(
    bool Ok, long FromSequence, long ToSequence, long HeadSequence, long VerifiedCount,
    long? FirstBrokenSequence, Guid? FirstBrokenEventId, string? Reason,
    CheckpointDto? CheckpointUsed, long PurgedSkeletons);

// ---------- Exports ----------

public sealed record EventFilterDto(
    DateTimeOffset? From, DateTimeOffset? To, string? ActorId, string? ActionPrefix,
    string? ResourceType, string? ResourceId, Outcome? Outcome, Category? Category, Guid? ProducerId);

public sealed record CreateExportRequest(EventFilterDto? Filter, ExportFormat Format = ExportFormat.Ndjson);

public sealed record ExportJobDto(
    Guid Id, ExportStatus Status, ExportFormat Format, EventFilterDto Filter, string RequestedBy,
    int Attempts, long RowCount, long SnapshotSequence, string? CurrentCursor, string? Checksum, string? Error,
    DateTime CreatedAt, DateTime? CompletedAt, DateTime? LeaseExpiresAt);

// ---------- Retention & legal holds ----------

public sealed record RetentionPolicyDto(Category Category, int RetentionDays, DateTime UpdatedAt);

public sealed record UpdateRetentionRequest(int RetentionDays);

public sealed record LegalHoldDto(
    Guid Id, HoldScopeType ScopeType, string ScopeValue, string Reason, string CreatedBy,
    DateTime CreatedAt, DateTime? ReleasedAt, string? ReleasedBy, bool Active);

public sealed record CreateLegalHoldRequest(HoldScopeType? ScopeType, string? ScopeValue, string? Reason);

public sealed record ReleaseLegalHoldRequest(string? Reason);

public sealed record RetentionRunResultDto(int Deleted);

// ---------- Summary ----------

public sealed record CategoryOutcomeCountDto(Category Category, Outcome Outcome, long Count);
public sealed record DeniedActorDto(ActorType ActorType, string ActorId, long Count);
public sealed record IngestionLagDto(double? P50Seconds, double? P95Seconds);
public sealed record ExportStatusCountDto(ExportStatus Status, long Count);

public sealed record SummaryDto(
    DateTime From, DateTime To, long TotalEvents,
    List<CategoryOutcomeCountDto> Counts, List<DeniedActorDto> TopDeniedActors, IngestionLagDto IngestionLag,
    CheckpointDto? LatestCheckpoint, long ActiveLegalHolds, List<ExportStatusCountDto> ExportsByStatus);
