using System.Net.Http.Json;
using System.Text.Json;
using AuditTrail.Api.Auth;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AuditTrail.Tests;

/// <summary>
/// One real PostgreSQL (Testcontainers) + the real API in memory (WebApplicationFactory) for all integration tests.
/// Workers are disabled so tests drive them explicitly. Every test creates its OWN tenant, so tests never interfere.
/// Requires Docker.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public string ConnectionString => _postgres.GetConnectionString();
    public string ExportDirectory { get; } = Path.Combine(Path.GetTempPath(), "audittrail-tests-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:AuditDb", ConnectionString);
            b.UseSetting("Database:SeedDemoData", "false");
            b.UseSetting("Export:WorkerEnabled", "false");
            b.UseSetting("Retention:WorkerEnabled", "false");
            b.UseSetting("Export:Directory", ExportDirectory);
            b.UseSetting("Export:ChunkSize", "10");
            b.UseSetting("Export:DelayPerChunkMs", "0");
            b.UseSetting("Search:CursorSigningKey", "test-key");
        });
        _ = Factory.Server;   // starts the app -> runs the migrations
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
        if (Directory.Exists(ExportDirectory)) Directory.Delete(ExportDirectory, recursive: true);
    }

    public T Service<T>() where T : notnull => Factory.Services.GetRequiredService<T>();

    public NpgsqlConnection Connection() => new(ConnectionString);

    /// <summary>Creates an isolated tenant with two active producers, one revoked producer, and the internal audit-platform producer.</summary>
    public async Task<TestTenant> CreateTenantAsync()
    {
        var t = new TestTenant(Guid.NewGuid(), "key-a-" + Guid.NewGuid().ToString("N"), "key-b-" + Guid.NewGuid().ToString("N"), "key-r-" + Guid.NewGuid().ToString("N"));
        await using var conn = Connection();
        await conn.ExecuteAsync("""
            INSERT INTO tenants (id, name, redaction_keys) VALUES (@Id, 'Test ' || @Id, '{password,cardNumber}');
            INSERT INTO chain_heads (tenant_id) VALUES (@Id);
            INSERT INTO producers (id, tenant_id, name, api_key_hash, status) VALUES
              (gen_random_uuid(), @Id, 'producer-a', @HashA, 'Active'),
              (gen_random_uuid(), @Id, 'producer-b', @HashB, 'Active'),
              (gen_random_uuid(), @Id, 'producer-revoked', @HashR, 'Revoked'),
              (gen_random_uuid(), @Id, 'audit-platform', @HashS, 'Active');
            """,
            new { t.Id, HashA = ApiKeyHasher.Hash(t.KeyA), HashB = ApiKeyHasher.Hash(t.KeyB), HashR = ApiKeyHasher.Hash(t.RevokedKey), HashS = ApiKeyHasher.Hash(Guid.NewGuid().ToString()) });
        return t;
    }

    public HttpClient Producer(string apiKey)
    {
        var c = Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return c;
    }

    public HttpClient Auditor(Guid tenantId, string role = "Auditor")
    {
        var c = Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        c.DefaultRequestHeaders.Add("X-Role", role);
        return c;
    }
}

public sealed record TestTenant(Guid Id, string KeyA, string KeyB, string RevokedKey);

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;

/// <summary>Builds valid event JSON for tests.</summary>
public static class Events
{
    public static Dictionary<string, object?> Valid(string eventId, DateTime? occurredAt = null, string category = "Access",
        string actorId = "user:alice", string action = "record.viewed", object? metadata = null, string resourceId = "R-1") => new()
    {
        ["eventId"] = eventId,
        ["occurredAt"] = (occurredAt ?? DateTime.UtcNow.AddSeconds(-1)).ToString("O"),
        ["actor"] = new { type = "User", id = actorId, ip = "10.0.0.1" },
        ["action"] = action,
        ["resource"] = new { type = "Record", id = resourceId },
        ["outcome"] = "Success",
        ["category"] = category,
        ["metadata"] = metadata ?? new { channel = "web" },
    };

    public static async Task<JsonElement> PostBatchAsync(HttpClient producer, IEnumerable<object> events)
    {
        var response = await producer.PostAsJsonAsync("/api/ingest/events/batch", new { events });
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<JsonElement> Json(this HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    public static string Iso(DateTime d) => Uri.EscapeDataString(d.ToString("O"));
}
