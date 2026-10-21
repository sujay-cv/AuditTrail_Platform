using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using AuditTrail.Api.Dtos;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;
using AuditTrail.Api.Repositories;

namespace AuditTrail.Api.Services;

/// <summary>
/// Ingestion: validate each event on its own, collapse repeats inside the batch, append the rest to the chain in ONE
/// transaction, and return a result per event.
///
/// Durability: this method returns only after COMMIT. So when the producer gets a 2xx, the events are durable.
/// On a timeout the producer does not know: it must retry with the SAME EventIds; already-stored events come back as "Duplicate"
/// with their original sequence. At-least-once delivery + idempotency on (tenant, producer, EventId) = exactly-once effect.
/// </summary>
public sealed class IngestionService(
    NpgsqlDataSource db, EventRepository events, TenantRepository tenants, EventValidator validator, AuditMetrics metrics)
{
    public async Task<List<EventResultDto>> IngestAsync(Guid tenantId, Guid producerId, IReadOnlyList<JsonElement> items, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var redactKeys = new HashSet<string>(await tenants.GetRedactionKeysAsync(tenantId, ct), StringComparer.OrdinalIgnoreCase);

        var results = new EventResultDto?[items.Count];
        var toAppend = new List<(int Index, NewEvent Event)>();
        var firstIndexOf = new Dictionary<string, int>();   // EventId -> index of its first occurrence in this batch
        var repeats = new List<(int Index, NewEvent Event)>();

        // 1. Validate every item independently
        for (var i = 0; i < items.Count; i++)
        {
            var (ev, failure) = validator.Validate(items[i], redactKeys);
            if (failure is not null)
            {
                results[i] = new EventResultDto(i, ReadEventId(items[i]), IngestStatus.Rejected, null, failure.Code, failure.Message, failure.Errors);
                metrics.Rejected(failure.Code);
            }
            else if (firstIndexOf.TryAdd(ev!.EventId, i)) toAppend.Add((i, ev));
            else repeats.Add((i, ev));
        }

        // 2. Append all valid, unique events in ONE transaction (one chain lock for the whole batch)
        if (toAppend.Count > 0)
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var appended = await events.AppendAsync(conn, tx, tenantId, producerId, toAppend.Select(a => a.Event).ToList(), ct);
            await tx.CommitAsync(ct);

            for (var k = 0; k < toAppend.Count; k++)
                results[toAppend[k].Index] = ToDto(toAppend[k].Index, toAppend[k].Event.EventId, appended[k]);
        }

        // 3. The same EventId appeared again later in the same batch: compare with the first occurrence
        foreach (var (index, ev) in repeats)
        {
            var firstIndex = firstIndexOf[ev.EventId];
            var first = results[firstIndex]!;
            var samePayload = toAppend.First(a => a.Index == firstIndex).Event.PayloadHash.AsSpan().SequenceEqual(ev.PayloadHash);
            results[index] = samePayload && first.Status != IngestStatus.Conflict
                ? new EventResultDto(index, ev.EventId, IngestStatus.Duplicate, first.Sequence, "duplicate_in_batch", "Same EventId and payload as an earlier item in this batch.", null)
                : new EventResultDto(index, ev.EventId, IngestStatus.Conflict, first.Sequence, "event_id_conflict", "Same EventId as an earlier item in this batch but a different payload.", null);
        }

        var final = results.Select(r => r!).ToList();
        metrics.Accepted(final.Count(r => r.Status == IngestStatus.Accepted));
        metrics.Duplicates(final.Count(r => r.Status == IngestStatus.Duplicate));
        metrics.Conflicts(final.Count(r => r.Status == IngestStatus.Conflict));
        metrics.IngestDuration(timer.Elapsed);
        return final;
    }

    private static EventResultDto ToDto(int index, string eventId, AppendResult r) => r.Outcome switch
    {
        AppendOutcome.Accepted => new(index, eventId, IngestStatus.Accepted, r.Sequence, null, null, null),
        AppendOutcome.Duplicate => new(index, eventId, IngestStatus.Duplicate, r.Sequence, "duplicate", "Already stored; this is the original sequence.", null),
        _ => new(index, eventId, IngestStatus.Conflict, r.Sequence, "event_id_conflict", "This EventId is already stored with a different payload.", null),
    };

    private static string? ReadEventId(JsonElement raw)
        => raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("eventId", out var id) && id.ValueKind == JsonValueKind.String
           && id.GetString() is { Length: <= 128 } s ? s : null;
}
