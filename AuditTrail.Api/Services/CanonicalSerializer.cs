using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuditTrail.Api.Models;

namespace AuditTrail.Api.Services;

public sealed class MetadataException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Canonical serialization for hashing (hash_version = 1).
///
/// WHY hand-written instead of JsonSerializer: the hashed bytes must never change when .NET or a library is upgraded,
/// otherwise old events would suddenly fail verification. Every rule is explicit here:
///
///  * An event is a JSON array of fields in a FIXED order, starting with the literal "v1" (see <see cref="EventBytes"/>).
///  * Strings escape only  "  \  and control characters (&lt; 0x20 as \u00xx). Everything else is written as-is, UTF-8.
///  * Timestamps are UTC "yyyy-MM-ddTHH:mm:ss.ffffffZ" (microseconds, because that is what PostgreSQL stores).
///  * Missing optional values are written as null.
///  * Metadata: object keys sorted (ordinal), no whitespace, numbers normalised, redacted values replaced by "[REDACTED]".
///    The metadata text is embedded as one string element.
///  * hash = SHA-256( prev_hash (32 raw bytes) || UTF-8(canonical array) ).  The first event's prev_hash is 32 zero bytes.
///
/// Changing any rule means adding hash_version = 2 and keeping this v1 code to verify old rows.
/// </summary>
public static class CanonicalSerializer
{
    public const int Version = 1;
    public const string RedactedValue = "[REDACTED]";
    public const int MaxDepth = 16;
    public static readonly byte[] GenesisHash = new byte[32];

    public static string FormatTimestamp(DateTime utc)
        => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Drops sub-microsecond ticks so the value hashed equals the value PostgreSQL stores.</summary>
    public static DateTime TruncateToMicroseconds(DateTime utc) => new(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);

    /// <summary>Canonical metadata JSON with redaction applied. Redaction keys match case-insensitively at any depth.</summary>
    public static string Metadata(JsonElement element, ISet<string> redactKeys)
    {
        var sb = new StringBuilder();
        WriteValue(sb, element, redactKeys, 0);
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, JsonElement e, ISet<string> redactKeys, int depth)
    {
        if (depth > MaxDepth) throw new MetadataException("metadata_too_deep", $"metadata may be nested at most {MaxDepth} levels.");

        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                // Duplicate keys: last one wins (the same rule PostgreSQL jsonb uses).
                var props = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject()) props[p.Name] = p.Value;
                sb.Append('{');
                var first = true;
                foreach (var (key, value) in props)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, key);
                    sb.Append(':');
                    if (redactKeys.Contains(key)) WriteString(sb, RedactedValue);
                    else WriteValue(sb, value, redactKeys, depth + 1);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    WriteValue(sb, item, redactKeys, depth + 1);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String: WriteString(sb, e.GetString()!); break;
            case JsonValueKind.Number: WriteNumber(sb, e); break;
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            case JsonValueKind.Null: sb.Append("null"); break;
            default: throw new MetadataException("metadata_invalid", "metadata contains an unsupported value.");
        }
    }

    /// <summary>
    /// Numbers go through a double so 1, 1.0 and 1e0 all become "1". This also makes the text identical after a round trip
    /// through PostgreSQL jsonb, which re-formats numbers.
    /// </summary>
    private static void WriteNumber(StringBuilder sb, JsonElement e)
    {
        if (!e.TryGetDouble(out var d) || !double.IsFinite(d))
            throw new MetadataException("metadata_invalid", "metadata contains a number that is out of range.");

        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
        else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        if (!IsWellFormed(s)) throw new MetadataException("metadata_invalid", "text contains invalid Unicode.");
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\0': throw new MetadataException("metadata_invalid", "text may not contain the NUL character.");
                case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
    }

    private static void WriteNullable(StringBuilder sb, string? s)
    {
        if (s is null) sb.Append("null");
        else WriteString(sb, s);
    }

    /// <summary>The canonical bytes of a stored event: everything the chain hash protects.</summary>
    public static byte[] EventBytes(AuditEvent e)
    {
        var sb = new StringBuilder(512);
        sb.Append('[');
        WriteString(sb, "v1"); sb.Append(',');
        WriteString(sb, e.Id.ToString("D")); sb.Append(',');
        WriteString(sb, e.TenantId.ToString("D")); sb.Append(',');
        WriteString(sb, e.ProducerId.ToString("D")); sb.Append(',');
        WriteString(sb, e.EventId); sb.Append(',');
        sb.Append(e.Sequence.ToString(CultureInfo.InvariantCulture)); sb.Append(',');
        WriteString(sb, FormatTimestamp(e.OccurredAt)); sb.Append(',');
        WriteString(sb, FormatTimestamp(e.ReceivedAt)); sb.Append(',');
        WriteString(sb, e.ActorType); sb.Append(',');
        WriteString(sb, e.ActorId); sb.Append(',');
        WriteNullable(sb, e.ActorIp); sb.Append(',');
        WriteNullable(sb, e.ActorUserAgent); sb.Append(',');
        WriteString(sb, e.Action); sb.Append(',');
        WriteString(sb, e.ResourceType); sb.Append(',');
        WriteString(sb, e.ResourceId); sb.Append(',');
        WriteString(sb, e.Outcome); sb.Append(',');
        WriteString(sb, e.Category); sb.Append(',');
        WriteString(sb, e.Metadata);
        sb.Append(']');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Fingerprint of what the producer sent (no server-assigned values). Used for idempotency:
    /// same EventId + same fingerprint = duplicate; same EventId + different fingerprint = conflict.
    /// </summary>
    public static byte[] PayloadHash(NewEvent e)
    {
        var sb = new StringBuilder(512);
        sb.Append('[');
        WriteString(sb, "p1"); sb.Append(',');
        WriteString(sb, e.EventId); sb.Append(',');
        WriteString(sb, FormatTimestamp(e.OccurredAt)); sb.Append(',');
        WriteString(sb, e.ActorType); sb.Append(',');
        WriteString(sb, e.ActorId); sb.Append(',');
        WriteNullable(sb, e.ActorIp); sb.Append(',');
        WriteNullable(sb, e.ActorUserAgent); sb.Append(',');
        WriteString(sb, e.Action); sb.Append(',');
        WriteString(sb, e.ResourceType); sb.Append(',');
        WriteString(sb, e.ResourceId); sb.Append(',');
        WriteString(sb, e.Outcome); sb.Append(',');
        WriteString(sb, e.Category); sb.Append(',');
        WriteString(sb, e.MetadataJson);
        sb.Append(']');
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>hash = SHA-256(prevHash || canonicalBytes)</summary>
    public static byte[] ChainHash(byte[] prevHash, byte[] canonicalBytes)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(prevHash);
        sha.AppendData(canonicalBytes);
        return sha.GetHashAndReset();
    }

    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>
    /// PostgreSQL jsonb re-formats JSON (adds spaces, re-orders keys), so the stored text is not byte-identical to what was hashed.
    /// Re-canonicalising it gives back exactly the canonical text, because canonicalisation is idempotent.
    /// </summary>
    public static string CanonicalizeStoredMetadata(string jsonbText)
    {
        using var doc = JsonDocument.Parse(jsonbText);
        return Metadata(doc.RootElement, EmptySet);
    }

    private static readonly ISet<string> EmptySet = new HashSet<string>();

    /// <summary>False for strings with unpaired UTF-16 surrogates (cannot be stored as UTF-8 text).</summary>
    public static bool IsWellFormed(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
            if (char.IsSurrogate(s[i])) return false;
        }
        return true;
    }
}
