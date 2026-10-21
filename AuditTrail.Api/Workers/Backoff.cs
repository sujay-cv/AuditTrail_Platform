namespace AuditTrail.Api.Workers;

/// <summary>Exponential backoff with jitter for transient failures (e.g. PostgreSQL down): ~1s, 2s, 4s ... capped at 60s.</summary>
public static class Backoff
{
    public static TimeSpan Delay(int consecutiveFailures)
    {
        var seconds = Math.Min(60, Math.Pow(2, Math.Min(consecutiveFailures, 10) - 1));
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;   // +/- 20% so many instances do not retry in lockstep
        return TimeSpan.FromSeconds(seconds * jitter);
    }

    /// <summary>Task.Delay that returns false instead of throwing when shutdown is requested.</summary>
    public static async Task<bool> SleepAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
