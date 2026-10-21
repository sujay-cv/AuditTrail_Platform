namespace AuditTrail.Api.Options;

// Bound from appsettings.json sections. Every value can be overridden with environment variables,
// e.g. Ingestion__MaxBatchSize=200.

public sealed class IngestionOptions
{
    public const string Section = "Ingestion";
    public int MaxBatchSize { get; set; } = 500;
    public int MaxMetadataBytes { get; set; } = 16 * 1024;
    /// <summary>How far in the future OccurredAt may be (producer clock skew allowance).</summary>
    public int FutureToleranceMinutes { get; set; } = 5;
    /// <summary>OccurredAt older than this is rejected as nonsense.</summary>
    public DateTime MinOccurredAt { get; set; } = new(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}

public sealed class SearchOptions
{
    public const string Section = "Search";
    public int MaxRangeDays { get; set; } = 90;
    public int DefaultPageSize { get; set; } = 50;
    public int MaxPageSize { get; set; } = 200;
    /// <summary>HMAC key that signs pagination cursors so a tampered cursor is rejected. Set via environment in production.</summary>
    public string CursorSigningKey { get; set; } = "";
}

public sealed class ExportOptions
{
    public const string Section = "Export";
    public bool WorkerEnabled { get; set; } = true;
    public string Directory { get; set; } = "exports";
    public int ChunkSize { get; set; } = 500;
    public int LeaseSeconds { get; set; } = 30;
    public int MaxAttempts { get; set; } = 3;
    public int PollSeconds { get; set; } = 2;
    /// <summary>Completed files are deleted (job -> Expired) after this many hours.</summary>
    public int FileRetentionHours { get; set; } = 24;
    /// <summary>Demo only: pause after every chunk so you can watch progress, cancel, or kill the worker mid-export.</summary>
    public int DelayPerChunkMs { get; set; } = 0;
}

public sealed class RetentionOptions
{
    public const string Section = "Retention";
    public bool WorkerEnabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 300;
    public int BatchSize { get; set; } = 500;
}
