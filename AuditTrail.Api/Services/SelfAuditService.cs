using System.Text.Json;
using Npgsql;
using AuditTrail.Api.Models;
using AuditTrail.Api.Repositories;

namespace AuditTrail.Api.Services;

/// <summary>
/// The platform audits itself: exports, retention changes, legal holds and purges are written as events into the
/// tenant's OWN chain, through the same EventRepository.AppendAsync (so they are hashed and sequenced like any other event).
/// Always called inside the caller's transaction, so "the action" and "the audit record of the action" commit together.
/// </summary>
public sealed class SelfAuditService(EventRepository events, TenantRepository tenants, TimeProvider clock)
{
    private static readonly ISet<string> NoRedaction = new HashSet<string>();

    public async Task RecordAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId,
        string actorId, string action, string resourceType, string resourceId, Category category,
        object metadata, CancellationToken ct)
    {
        var producerId = await tenants.GetSystemProducerIdAsync(conn, tx, tenantId, ct);
        var metadataJson = CanonicalSerializer.Metadata(JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web), NoRedaction);

        var ev = new NewEvent(
            EventId: "platform-" + Guid.CreateVersion7(),
            OccurredAt: CanonicalSerializer.TruncateToMicroseconds(clock.GetUtcNow().UtcDateTime),
            ActorType: actorId.StartsWith("system:", StringComparison.Ordinal) ? nameof(ActorType.System) : nameof(ActorType.User),
            ActorId: actorId, ActorIp: null, ActorUserAgent: null,
            Action: action, ResourceType: resourceType, ResourceId: resourceId,
            Outcome: nameof(Outcome.Success), Category: category.ToString(),
            MetadataJson: metadataJson, PayloadHash: []);

        await events.AppendAsync(conn, tx, tenantId, producerId, [ev with { PayloadHash = CanonicalSerializer.PayloadHash(ev) }], ct);
    }
}
