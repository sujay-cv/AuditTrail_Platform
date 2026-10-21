using System.Text.Json;
using Dapper;
using Npgsql;
using AuditTrail.Api.Auth;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;
using AuditTrail.Api.Repositories;
using AuditTrail.Api.Services;

namespace AuditTrail.Api.Data;

/// <summary>
/// Creates reproducible demo data on an EMPTY database (it does nothing if tenants already exist):
///  * 2 tenants, 4 active producers each (+ 1 revoked producer, + the internal "audit-platform" producer)
///  * ~700 events per tenant over the last 150 days, all categories and outcomes
///  * the same EventId in both tenants ("evt-shared-0001")                        -> proves tenant isolation
///  * an event whose metadata contains &lt;script&gt; ("evt-xss-0001")                 -> proves the UI renders text, not HTML
///  * metadata with password / cardNumber / ssn                                   -> redacted before storage
///  * 25 late-arriving events (old OccurredAt, stored last with a high Sequence)
///  * Access retention = 90 days (Acme) / 60 days (Globex) -> some events are purgeable right away
///  * an active legal hold on actor "user:jdoe" (Acme) -> protects some of those events
/// Everything is written in ONE transaction: a crash mid-seed leaves an empty database and the next start seeds again.
///
/// DEMO API KEYS ARE NOT SECRETS: they exist only in this local demo and are stored hashed like real keys.
/// </summary>
public sealed class DbSeeder(NpgsqlDataSource db, EventRepository events, EventValidator validator, ILogger<DbSeeder> logger)
{
    public static readonly Guid AcmeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid GlobexId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly string[] RedactionKeys = ["password", "ssn", "cardNumber", "apiToken", "secret"];

    private sealed record ProducerSeed(string Name, string ApiKey, string Status, Category[] Categories);

    private static readonly Dictionary<Guid, (string Name, ProducerSeed[] Producers, int AccessRetentionDays)> Tenants = new()
    {
        [AcmeId] = ("Acme Bank",
        [
            new("payments-service", "acme-payments-key", "Active", [Category.Financial]),
            new("identity-service", "acme-identity-key", "Active", [Category.Security, Category.Access]),
            new("hr-service", "acme-hr-key", "Active", [Category.DataChange, Category.Access]),
            new("admin-console", "acme-admin-key", "Active", [Category.Configuration, Category.Security]),
            new("legacy-batch", "acme-revoked-key", "Revoked", [Category.DataChange]),
        ], 90),
        [GlobexId] = ("Globex Insurance",
        [
            new("claims-service", "globex-claims-key", "Active", [Category.Financial, Category.DataChange]),
            new("identity-service", "globex-identity-key", "Active", [Category.Security, Category.Access]),
            new("billing-service", "globex-billing-key", "Active", [Category.Financial, Category.Configuration]),
            new("admin-console", "globex-admin-key", "Active", [Category.Configuration, Category.Access]),
            new("legacy-batch", "globex-revoked-key", "Revoked", [Category.DataChange]),
        ], 60),
    };

    private static readonly Dictionary<Category, (string Action, string ResourceType)[]> Actions = new()
    {
        [Category.Security] = [("user.login", "User"), ("user.logout", "User"), ("user.mfa.challenged", "User"), ("user.password.reset", "User")],
        [Category.Access] = [("customer.profile.viewed", "Customer"), ("report.downloaded", "Report"), ("record.viewed", "Record")],
        [Category.DataChange] = [("customer.updated", "Customer"), ("invoice.created", "Invoice"), ("invoice.updated", "Invoice")],
        [Category.Configuration] = [("user.role.granted", "User"), ("feature.flag.changed", "Setting"), ("user.role.revoked", "User")],
        [Category.Financial] = [("payment.approved", "Payment"), ("payment.rejected", "Payment"), ("refund.issued", "Refund"), ("transfer.initiated", "Transfer")],
    };

    private static readonly string[] Users = ["user:jdoe", "user:asmith", "user:mkhan", "user:lchen", "user:rpatel", "user:okafor"];

    public async Task SeedAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Serialise seeding across instances; then skip if already seeded.
        await conn.ExecuteAsync(Database.Cmd("SELECT pg_advisory_xact_lock(727274002)", tx: tx, ct: ct));
        if (await conn.ExecuteScalarAsync<bool>(Database.Cmd("SELECT EXISTS (SELECT 1 FROM tenants)", tx: tx, ct: ct)))
            return;

        var random = new Random(42);   // fixed seed = same data every time
        var now = DateTime.UtcNow;

        foreach (var (tenantId, tenant) in Tenants)
        {
            await conn.ExecuteAsync(Database.Cmd(
                "INSERT INTO tenants (id, name, redaction_keys) VALUES (@tenantId, @name, @keys); INSERT INTO chain_heads (tenant_id) VALUES (@tenantId);",
                new { tenantId, name = tenant.Name, keys = RedactionKeys }, tx, ct));

            var producerIds = new Dictionary<string, Guid>();
            foreach (var p in tenant.Producers.Append(new ProducerSeed(TenantRepository.SystemProducerName, Guid.NewGuid().ToString(), "Active", [])))
            {
                var id = Guid.CreateVersion7();
                producerIds[p.Name] = id;
                await conn.ExecuteAsync(Database.Cmd(
                    "INSERT INTO producers (id, tenant_id, name, api_key_hash, status) VALUES (@id, @tenantId, @name, @hash, @status)",
                    new { id, tenantId, name = p.Name, hash = ApiKeyHasher.Hash(p.ApiKey), status = p.Status }, tx, ct));
            }

            foreach (var (category, days) in new[] {
                (Category.Security, 2555), (Category.Access, tenant.AccessRetentionDays), (Category.DataChange, 365),
                (Category.Configuration, 730), (Category.Financial, 2555) })
            {
                await conn.ExecuteAsync(Database.Cmd(
                    "INSERT INTO retention_policies (tenant_id, category, retention_days) VALUES (@tenantId, @category, @days)",
                    new { tenantId, category = category.ToString(), days }, tx, ct));
            }

            if (tenantId == AcmeId)
                await conn.ExecuteAsync(Database.Cmd(
                    "INSERT INTO legal_holds (id, tenant_id, scope_type, scope_value, reason, created_by) VALUES (@id, @tenantId, 'Actor', 'user:jdoe', @reason, 'seed')",
                    new { id = Guid.CreateVersion7(), tenantId, reason = "Litigation hold: matter 2026-17 (demo)" }, tx, ct));

            var redact = new HashSet<string>(RedactionKeys, StringComparer.OrdinalIgnoreCase);
            var active = tenant.Producers.Where(p => p.Status == "Active").ToArray();

            // ~700 events per tenant spread over 150 days, in OccurredAt order, each "received" a few seconds after it occurred.
            // Producers take turns in blocks of 25 events, so the seed needs few (fast) appends.
            var planned = new List<(ProducerSeed Producer, NewEvent Event)>();
            var producer = active[0];
            for (var i = 0; i < 700; i++)
            {
                if (i % 25 == 0) producer = active[random.Next(active.Length)];
                var occurredAt = now.AddDays(-150).AddMinutes(i * (150.0 * 24 * 60 / 700) + random.Next(0, 60));
                var raw = RandomEvent(random, producer, $"{producer.Name}-{i:D5}", occurredAt, i);
                planned.Add((producer, Validate(raw, redact) with { ReceivedAtOverride = occurredAt.AddSeconds(random.Next(1, 30)) }));
            }

            // Special events
            var first = active[0];
            planned.Add((first, Validate(new
            {
                eventId = "evt-shared-0001", occurredAt = now.AddDays(-2), actor = new { type = "User", id = "user:asmith" },
                action = "payment.approved", resource = new { type = "Payment", id = $"PAY-SHARED-{tenant.Name[..4].ToUpperInvariant()}" },
                outcome = "Success", category = "Financial", metadata = new { note = $"Same EventId exists in the other tenant; this one belongs to {tenant.Name}" },
            }, redact) with { ReceivedAtOverride = now.AddDays(-2).AddSeconds(3) }));

            planned.Add((active[1], Validate(new
            {
                eventId = "evt-xss-0001", occurredAt = now.AddDays(-1), actor = new { type = "User", id = "user:mallory", ip = "203.0.113.66", userAgent = "<b>bold-agent</b>" },
                action = "customer.profile.viewed", resource = new { type = "Customer", id = "CUS-<script>" },
                outcome = "Denied", category = "Access",
                metadata = new
                {
                    comment = "<script>alert('xss')</script>",
                    html = "<img src=x onerror=alert(document.cookie)>",
                    password = "hunter2",
                    nested = new { cardNumber = "4111111111111111", safe = "visible" },
                },
            }, redact) with { ReceivedAtOverride = now.AddDays(-1).AddSeconds(2) }));

            // Late arrivals: happened 100-140 days ago, but received NOW -> they get the highest sequence numbers.
            var lateProducer = active[random.Next(active.Length)];
            for (var i = 0; i < 25; i++)
            {
                var raw = RandomEvent(random, lateProducer, $"{lateProducer.Name}-late-{i:D3}", now.AddDays(-140 + i * 1.5), 10_000 + i);
                planned.Add((lateProducer, Validate(raw, redact) with { ReceivedAtOverride = now.AddMinutes(-30 + i) }));
            }

            // Append in planned order (consecutive events from the same producer go together), so ReceivedAt stays realistic.
            var run = new List<NewEvent>();
            for (var i = 0; i < planned.Count; i++)
            {
                run.Add(planned[i].Event);
                var producerChanges = i == planned.Count - 1 || planned[i + 1].Producer != planned[i].Producer;
                if (!producerChanges) continue;
                await events.AppendAsync(conn, tx, tenantId, producerIds[planned[i].Producer.Name], run, ct);
                run = [];
            }
        }

        await tx.CommitAsync(ct);
        logger.LogInformation("Seeded demo data: 2 tenants, {Count} events", Tenants.Count * 727);
    }

    private NewEvent Validate(object raw, ISet<string> redact)
    {
        var (ev, failure) = validator.Validate(JsonSerializer.SerializeToElement(raw, JsonSerializerOptions.Web), redact);
        return ev ?? throw new InvalidOperationException("Seed event is invalid: " + failure!.Message);
    }

    private static object RandomEvent(Random random, ProducerSeed producer, string eventId, DateTime occurredAt, int n)
    {
        var category = producer.Categories[random.Next(producer.Categories.Length)];
        var (action, resourceType) = Actions[category][random.Next(Actions[category].Length)];
        var roll = random.Next(100);
        var outcome = roll < 70 ? "Success" : roll < 85 ? "Failure" : "Denied";
        var isService = random.Next(10) == 0;
        var actorId = isService ? $"svc:{producer.Name}" : Users[random.Next(Users.Length)];

        object metadata = category switch
        {
            Category.Financial => new { amount = Math.Round(random.NextDouble() * 5000, 2), currency = "USD", channel = "api", cardNumber = "4111111111111111" },
            Category.DataChange => new { changedFields = new[] { "email", "phone" }, ssn = "123-45-6789", requestId = $"req-{n}" },
            Category.Security => new { method = random.Next(2) == 0 ? "password" : "sso", password = "not-really", attempts = random.Next(1, 4) },
            Category.Configuration => new { previous = "off", current = "on", ticket = $"CHG-{1000 + n}" },
            _ => new { channel = "web", requestId = $"req-{n}" },
        };

        return new
        {
            eventId,
            occurredAt,
            actor = new
            {
                type = isService ? "Service" : "User",
                id = actorId,
                ip = isService ? null : $"10.{random.Next(0, 255)}.{random.Next(0, 255)}.{random.Next(1, 254)}",
                userAgent = isService ? null : "Mozilla/5.0 (demo)",
            },
            action,
            resource = new { type = resourceType, id = $"{resourceType[..3].ToUpperInvariant()}-{random.Next(10000, 99999)}" },
            outcome,
            category = category.ToString(),
            metadata,
        };
    }
}
