using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using AuditTrail.Api.Dtos;
using AuditTrail.Api.Models;
using AuditTrail.Api.Options;

namespace AuditTrail.Api.Services;

public sealed record ValidationFailure(string Code, string Message, Dictionary<string, string[]> Errors);

/// <summary>
/// Validates ONE raw event at the API boundary and turns it into a <see cref="NewEvent"/>:
/// required fields, formats, size limits, clock rules, then metadata redaction + canonicalisation.
/// Pure logic, no database, so it is easy to unit test.
/// </summary>
public sealed partial class EventValidator(IOptions<IngestionOptions> options, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IngestionOptions _opt = options.Value;

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex IsoWithOffset();

    [GeneratedRegex(@"^[a-z][a-z0-9_-]*(\.[a-z0-9_-]+)+$")]
    private static partial Regex DottedAction();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.:-]{0,63}$")]
    private static partial Regex ResourceTypeFormat();

    /// <summary>Returns either the event or a failure (never both).</summary>
    public (NewEvent? Event, ValidationFailure? Failure) Validate(JsonElement raw, ISet<string> redactKeys)
    {
        EventInput? input = null;
        if (raw.ValueKind == JsonValueKind.Object)
        {
            try { input = raw.Deserialize<EventInput>(Json); }
            catch (JsonException) { /* wrong JSON types, e.g. "actor": 5 */ }
        }
        if (input is null)
            return (null, new ValidationFailure("malformed_event", "The event must be a JSON object with correctly typed fields.",
                new() { ["event"] = ["The event must be a JSON object with correctly typed fields."] }));

        var errors = new List<(string Field, string Code, string Message)>();
        void Error(string field, string code, string message) => errors.Add((field, code, message));

        // eventId
        if (string.IsNullOrWhiteSpace(input.EventId)) Error("eventId", "required", "eventId is required.");
        else if (input.EventId.Length > 128) Error("eventId", "too_long", "eventId must be at most 128 characters.");
        else if (!IsCleanText(input.EventId)) Error("eventId", "invalid_format", "eventId contains invalid characters.");

        // occurredAt: ISO-8601 with an explicit offset, not too far in the future, not absurdly old
        var occurredAt = default(DateTime);
        if (string.IsNullOrWhiteSpace(input.OccurredAt)) Error("occurredAt", "required", "occurredAt is required.");
        else if (!IsoWithOffset().IsMatch(input.OccurredAt) ||
                 !DateTimeOffset.TryParse(input.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            Error("occurredAt", "invalid_format", "occurredAt must be ISO-8601 with an offset, e.g. 2026-10-19T10:15:30Z.");
        else
        {
            occurredAt = CanonicalSerializer.TruncateToMicroseconds(parsed.UtcDateTime);
            var now = clock.GetUtcNow().UtcDateTime;
            if (occurredAt > now.AddMinutes(_opt.FutureToleranceMinutes))
                Error("occurredAt", "occurred_at_in_future", $"occurredAt may be at most {_opt.FutureToleranceMinutes} minutes in the future.");
            else if (occurredAt < _opt.MinOccurredAt)
                Error("occurredAt", "occurred_at_too_old", $"occurredAt must be on or after {_opt.MinOccurredAt:yyyy-MM-dd}.");
        }

        // actor
        string? actorType = null, actorIp = null, actorUserAgent = null;
        var actorId = input.Actor?.Id;
        if (input.Actor is null) Error("actor", "required", "actor is required.");
        else
        {
            actorType = ParseEnum<ActorType>(input.Actor.Type);
            if (actorType is null) Error("actor.type", "invalid_value", "actor.type must be User, Service or System.");

            if (string.IsNullOrWhiteSpace(actorId)) Error("actor.id", "required", "actor.id is required.");
            else if (actorId.Length > 128) Error("actor.id", "too_long", "actor.id must be at most 128 characters.");
            else if (!IsCleanText(actorId)) Error("actor.id", "invalid_format", "actor.id contains invalid characters.");

            if (!string.IsNullOrEmpty(input.Actor.Ip))
            {
                if (input.Actor.Ip.Length > 45 || !IPAddress.TryParse(input.Actor.Ip, out var ip))
                    Error("actor.ip", "invalid_format", "actor.ip must be an IPv4 or IPv6 address.");
                else actorIp = ip.ToString();
            }
            if (!string.IsNullOrEmpty(input.Actor.UserAgent))
            {
                if (input.Actor.UserAgent.Length > 512) Error("actor.userAgent", "too_long", "actor.userAgent must be at most 512 characters.");
                else if (!IsCleanText(input.Actor.UserAgent)) Error("actor.userAgent", "invalid_format", "actor.userAgent contains invalid characters.");
                else actorUserAgent = input.Actor.UserAgent;
            }
        }

        // action: dotted verb
        if (string.IsNullOrWhiteSpace(input.Action)) Error("action", "required", "action is required.");
        else if (input.Action.Length > 128) Error("action", "too_long", "action must be at most 128 characters.");
        else if (!DottedAction().IsMatch(input.Action)) Error("action", "invalid_format", "action must be a dotted lowercase verb, e.g. payment.approved.");

        // resource
        var resourceType = input.Resource?.Type;
        var resourceId = input.Resource?.Id;
        if (input.Resource is null) Error("resource", "required", "resource is required.");
        else
        {
            if (string.IsNullOrWhiteSpace(resourceType)) Error("resource.type", "required", "resource.type is required.");
            else if (!ResourceTypeFormat().IsMatch(resourceType)) Error("resource.type", "invalid_format", "resource.type must be 1-64 letters, digits, '_', '.', ':' or '-'.");

            if (string.IsNullOrWhiteSpace(resourceId)) Error("resource.id", "required", "resource.id is required.");
            else if (resourceId.Length > 256) Error("resource.id", "too_long", "resource.id must be at most 256 characters.");
            else if (!IsCleanText(resourceId)) Error("resource.id", "invalid_format", "resource.id contains invalid characters.");
        }

        // outcome, category
        var outcome = ParseEnum<Outcome>(input.Outcome);
        if (outcome is null) Error("outcome", input.Outcome is null ? "required" : "invalid_value", "outcome must be Success, Failure or Denied.");
        var category = ParseEnum<Category>(input.Category);
        if (category is null) Error("category", input.Category is null ? "required" : "invalid_value", "category must be Security, Access, DataChange, Configuration or Financial.");

        // metadata: optional JSON object, max 16 KB as sent; redacted + canonicalised BEFORE storage and hashing
        var metadataJson = "{}";
        if (input.Metadata is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } metadata)
        {
            if (metadata.ValueKind != JsonValueKind.Object) Error("metadata", "metadata_not_object", "metadata must be a JSON object.");
            else if (Encoding.UTF8.GetByteCount(metadata.GetRawText()) > _opt.MaxMetadataBytes)
                Error("metadata", "metadata_too_large", $"metadata must be at most {_opt.MaxMetadataBytes} bytes.");
            else
            {
                try { metadataJson = CanonicalSerializer.Metadata(metadata, redactKeys); }
                catch (MetadataException ex) { Error("metadata", ex.Code, ex.Message); }
            }
        }

        if (errors.Count > 0)
        {
            var fieldErrors = errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray());
            return (null, new ValidationFailure(errors[0].Code, errors[0].Message, fieldErrors));
        }

        var ev = new NewEvent(input.EventId!, occurredAt, actorType!, actorId!, actorIp, actorUserAgent,
            input.Action!, resourceType!, resourceId!, outcome!, category!, metadataJson, []);
        return (ev with { PayloadHash = CanonicalSerializer.PayloadHash(ev) }, null);
    }

    private static string? ParseEnum<T>(string? value) where T : struct, Enum
        => Enum.GetNames<T>().FirstOrDefault(n => n.Equals(value, StringComparison.OrdinalIgnoreCase));

    private static bool IsCleanText(string s) => !s.Any(char.IsControl) && CanonicalSerializer.IsWellFormed(s);
}
