using AuditTrail.Api.Dtos;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Repositories;

namespace AuditTrail.Api.Services;

/// <summary>
/// Walks a tenant's chain in Sequence order and recomputes every hash.
///
///  * Start: the newest checkpoint BEFORE the requested range (trusted: it was verified when written), or the genesis (zero hash).
///  * For each sequence n:
///      - row present:   prev_hash must equal the previous hash, and SHA-256(prev || canonical(row)) must equal row.hash
///      - purged:        only the link can be checked (prev_hash == previous hash); content is gone by design
///      - neither:       the row was deleted outside the retention path -> broken at n
///  * The first sequence that fails is reported. Modified content, a deleted row, or two swapped rows all fail at that exact sequence.
/// </summary>
public sealed class IntegrityService(EventRepository events, CheckpointRepository checkpoints, AuditMetrics metrics)
{
    private const int ChunkSize = 2000;

    public async Task<VerifyResultDto> VerifyAsync(Guid tenantId, long? fromSequence, long? toSequence, CancellationToken ct)
    {
        var head = await events.GetHeadAsync(tenantId, ct) ?? throw ApiException.NotFound("Tenant chain");
        var from = fromSequence ?? 1;
        var to = Math.Min(toSequence ?? head.LastSequence, head.LastSequence);
        if (from < 1) throw ApiException.Field("fromSequence", "invalid_range", "fromSequence must be at least 1.");
        if (toSequence is not null && toSequence < from) throw ApiException.Field("toSequence", "invalid_range", "toSequence must be >= fromSequence.");

        var checkpoint = await checkpoints.LatestBeforeAsync(tenantId, from, ct);
        var expectedPrev = checkpoint?.Hash ?? CanonicalSerializer.GenesisHash;
        var start = (checkpoint?.Sequence ?? 0) + 1;

        long verified = 0, purgedCount = 0;
        for (var chunkStart = start; chunkStart <= to; chunkStart += ChunkSize)
        {
            var chunkEnd = Math.Min(chunkStart + ChunkSize - 1, to);
            var (rows, purged) = await events.ReadChainRangeAsync(tenantId, chunkStart, chunkEnd, ct);
            var rowBySeq = rows.ToDictionary(r => r.Sequence);
            var purgedBySeq = purged.ToDictionary(p => p.Sequence);

            for (var seq = chunkStart; seq <= chunkEnd; seq++)
            {
                string? problem = null;
                Guid? eventId = null;

                if (rowBySeq.TryGetValue(seq, out var row))
                {
                    eventId = row.Id;
                    if (!row.PrevHash.AsSpan().SequenceEqual(expectedPrev)) problem = "prev_hash_mismatch";
                    else if (row.HashVersion != CanonicalSerializer.Version) problem = "unknown_hash_version";
                    else if (!Recompute(row, expectedPrev).AsSpan().SequenceEqual(row.Hash)) problem = "content_modified";
                    expectedPrev = row.Hash;
                }
                else if (purgedBySeq.TryGetValue(seq, out var skeleton))
                {
                    if (!skeleton.PrevHash.AsSpan().SequenceEqual(expectedPrev)) problem = "prev_hash_mismatch";
                    expectedPrev = skeleton.Hash;
                    purgedCount++;
                }
                else problem = "missing";

                if (problem is not null)
                {
                    metrics.VerificationFailure();
                    return Result(false, seq, eventId, problem);
                }
                verified++;
            }
        }

        // The whole chain was walked: the last hash must also match the chain head.
        if (to == head.LastSequence && to > 0 && !expectedPrev.AsSpan().SequenceEqual(head.LastHash))
        {
            metrics.VerificationFailure();
            return Result(false, to, null, "head_mismatch");
        }
        return Result(true, null, null, null);

        VerifyResultDto Result(bool ok, long? brokenAt, Guid? brokenEventId, string? reason) => new(
            ok, from, to, head.LastSequence, verified, brokenAt, brokenEventId, reason,
            checkpoint is null ? null : Mappers.ToDto(checkpoint), purgedCount);
    }

    private static byte[] Recompute(Models.AuditEvent row, byte[] prevHash)
    {
        try
        {
            row.Metadata = CanonicalSerializer.CanonicalizeStoredMetadata(row.Metadata);
            return CanonicalSerializer.ChainHash(prevHash, CanonicalSerializer.EventBytes(row));
        }
        catch (Exception ex) when (ex is MetadataException or System.Text.Json.JsonException)
        {
            return [];   // unreadable metadata can only come from tampering -> reported as content_modified
        }
    }

    /// <summary>
    /// Used by the retention worker: verify from the latest checkpoint up to the head and, if intact, write a new checkpoint there.
    /// Returns false (and writes nothing) if the chain is broken.
    /// </summary>
    public async Task<bool> CreateCheckpointAsync(Guid tenantId, CancellationToken ct)
    {
        var latest = await checkpoints.LatestAsync(tenantId, ct);
        var head = await events.GetHeadAsync(tenantId, ct);
        if (head is null || head.LastSequence == 0 || head.LastSequence == latest?.Sequence) return true;

        var result = await VerifyAsync(tenantId, (latest?.Sequence ?? 0) + 1, head.LastSequence, ct);
        if (!result.Ok) return false;

        await checkpoints.InsertAsync(tenantId, head.LastSequence, head.LastHash, ct);
        return true;
    }
}
