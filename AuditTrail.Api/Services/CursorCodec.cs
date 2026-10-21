using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;
using AuditTrail.Api.Options;

namespace AuditTrail.Api.Services;

/// <summary>
/// Opaque pagination cursor = base64url(JSON payload) + "." + base64url(HMAC-SHA256(payload)).
/// The payload holds the last row's (OccurredAt, Sequence), the snapshot MaxSequence and a hash of the filter it was issued for.
/// A tampered cursor fails the HMAC check; a cursor reused with a different filter fails the filter check. Both are 400.
/// </summary>
public sealed class CursorCodec
{
    private sealed record Payload(long T, long S, long M, string F);

    private readonly byte[] _key;

    public CursorCodec(IOptions<SearchOptions> options, ILogger<CursorCodec> logger)
    {
        var configured = options.Value.CursorSigningKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            // Fine for a single instance; with several instances they must share a key (Search__CursorSigningKey).
            logger.LogWarning("Search:CursorSigningKey is not set; using a random key (cursors will not survive a restart).");
            _key = RandomNumberGenerator.GetBytes(32);
        }
        else _key = Encoding.UTF8.GetBytes(configured);
    }

    public string Encode(EventCursor position, long maxSequence, EventFilter filter, bool newestFirst)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Payload(position.OccurredAt.Ticks, position.Sequence, maxSequence, FilterHash(filter, newestFirst)));
        return Base64Url(json) + "." + Base64Url(HMACSHA256.HashData(_key, json));
    }

    public (EventCursor Position, long MaxSequence) Decode(string cursor, EventFilter filter, bool newestFirst)
    {
        var invalid = ApiException.Field("cursor", "invalid_cursor", "The cursor is invalid or has been tampered with. Restart the search from the first page.");
        var parts = cursor.Split('.');
        if (parts.Length != 2 || cursor.Length > 512) throw invalid;

        byte[] json, signature;
        try { json = FromBase64Url(parts[0]); signature = FromBase64Url(parts[1]); }
        catch (FormatException) { throw invalid; }

        if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(_key, json))) throw invalid;

        var p = JsonSerializer.Deserialize<Payload>(json) ?? throw invalid;
        if (p.F != FilterHash(filter, newestFirst))
            throw ApiException.Field("cursor", "cursor_filter_mismatch", "The cursor belongs to a different filter. Restart the search from the first page.");

        return (new EventCursor(new DateTime(p.T, DateTimeKind.Utc), p.S), p.M);
    }

    /// <summary>The cursor is bound to the filter AND the sort direction it was issued for.</summary>
    private static string FilterHash(EventFilter filter, bool newestFirst)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { filter, newestFirst })))[..16];

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
