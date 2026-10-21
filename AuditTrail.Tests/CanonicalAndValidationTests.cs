using System.Text;
using System.Text.Json;
using AuditTrail.Api.Models;
using AuditTrail.Api.Options;
using AuditTrail.Api.Services;
using Microsoft.Extensions.Options;

namespace AuditTrail.Tests;

/// <summary>Pure unit tests: canonical serialization, redaction and boundary validation (no database).</summary>
public sealed class CanonicalAndValidationTests
{
    private static readonly ISet<string> Redact = new HashSet<string>(["password", "cardNumber"], StringComparer.OrdinalIgnoreCase);
    private readonly EventValidator _validator = new(Options.Create(new IngestionOptions()), TimeProvider.System);

    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o, JsonSerializerOptions.Web);

    [Fact]
    public void Metadata_is_sorted_compact_and_redacted_at_any_depth()
    {
        var nested = new Dictionary<string, object> { ["PASSWORD"] = "secret", ["keep"] = true };   // dictionary keeps the key's case
        var canonical = CanonicalSerializer.Metadata(J(new { z = 1.0, a = "x", nested }), Redact);
        Assert.Equal("""{"a":"x","nested":{"PASSWORD":"[REDACTED]","keep":true},"z":1}""", canonical);
    }

    [Fact]
    public void Canonical_metadata_survives_a_jsonb_style_reformat()
    {
        var canonical = CanonicalSerializer.Metadata(J(new { b = 2.5, a = new[] { 1, 2 } }), Redact);
        // PostgreSQL jsonb returns keys in its own order with spaces: re-canonicalising must give the same text.
        Assert.Equal(canonical, CanonicalSerializer.CanonicalizeStoredMetadata("""{"a": [1, 2], "b": 2.5}"""));
    }

    [Fact]
    public void Chain_hash_is_stable_for_a_known_event()
    {
        var e = new AuditEvent
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), TenantId = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            ProducerId = Guid.Parse("00000000-0000-0000-0000-000000000003"), EventId = "e-1", Sequence = 1,
            OccurredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), ReceivedAt = new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc),
            ActorType = "User", ActorId = "u", Action = "a.b", ResourceType = "R", ResourceId = "1", Outcome = "Success", Category = "Access", Metadata = "{}",
        };
        var bytes = CanonicalSerializer.EventBytes(e);
        Assert.Equal("""["v1","00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002","00000000-0000-0000-0000-000000000003","e-1",1,"2026-01-01T00:00:00.000000Z","2026-01-01T00:00:01.000000Z","User","u",null,null,"a.b","R","1","Success","Access","{}"]""",
            Encoding.UTF8.GetString(bytes));
        // Golden value. If this ever changes, every stored hash breaks: that requires a new hash_version, not an edit.
        Assert.Equal("a59254ab0c8ad681c88dbf0c1c049fa616a4254cb423b0f182a7aef637f61c04", CanonicalSerializer.Hex(CanonicalSerializer.ChainHash(CanonicalSerializer.GenesisHash, bytes)));
    }

    [Fact]
    public void Valid_event_is_accepted_and_metadata_redacted()
    {
        var (ev, failure) = _validator.Validate(J(Events.Valid("e-1", metadata: new { password = "p", cardNumber = "4111", ok = 1 })), Redact);
        Assert.Null(failure);
        Assert.Equal("""{"cardNumber":"[REDACTED]","ok":1,"password":"[REDACTED]"}""", ev!.MetadataJson);
        Assert.Equal(32, ev.PayloadHash.Length);
    }

    [Theory]
    [InlineData("eventId", "required")]
    [InlineData("actor", "required")]
    [InlineData("occurredAt", "required")]
    [InlineData("resource", "required")]
    public void Missing_required_field_is_rejected(string field, string code)
    {
        var e = Events.Valid("e-1");
        e.Remove(field);
        var (_, failure) = _validator.Validate(J(e), Redact);
        Assert.NotNull(failure);
        Assert.Equal(code, failure!.Code);
        Assert.True(failure.Errors.ContainsKey(field));
    }

    [Fact]
    public void Future_occurred_at_beyond_tolerance_is_rejected()
    {
        var (_, failure) = _validator.Validate(J(Events.Valid("e-1", DateTime.UtcNow.AddMinutes(10))), Redact);
        Assert.Equal("occurred_at_in_future", failure!.Code);
    }

    [Fact]
    public void Unsupported_category_and_outcome_are_rejected()
    {
        var e = Events.Valid("e-1");
        e["category"] = "Gossip";
        e["outcome"] = "Maybe";
        var (_, failure) = _validator.Validate(J(e), Redact);
        Assert.Contains("category", failure!.Errors.Keys);
        Assert.Contains("outcome", failure.Errors.Keys);
    }

    [Fact]
    public void Oversized_metadata_is_rejected()
    {
        var (_, failure) = _validator.Validate(J(Events.Valid("e-1", metadata: new { blob = new string('x', 17_000) })), Redact);
        Assert.Equal("metadata_too_large", failure!.Code);
    }

    [Fact]
    public void Malformed_event_is_rejected()
    {
        var (_, failure) = _validator.Validate(J(42), Redact);
        Assert.Equal("malformed_event", failure!.Code);
    }
}
