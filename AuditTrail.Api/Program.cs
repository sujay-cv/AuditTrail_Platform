using System.Text.Json.Serialization;
using AuditTrail.Api.Auth;
using AuditTrail.Api.Controllers;
using AuditTrail.Api.Data;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;
using AuditTrail.Api.Options;
using AuditTrail.Api.Repositories;
using AuditTrail.Api.Services;
using AuditTrail.Api.Workers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------------------------------------------------------------------------------------------
// Logging: structured, with scopes (CorrelationId, TenantId, ProducerId, JobId) on every line.
// ---------------------------------------------------------------------------------------------
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

// ---------------------------------------------------------------------------------------------
// Options (appsettings.json sections, overridable with environment variables like Export__ChunkSize=1000)
// ---------------------------------------------------------------------------------------------
builder.Services.Configure<IngestionOptions>(config.GetSection(IngestionOptions.Section));
builder.Services.Configure<SearchOptions>(config.GetSection(SearchOptions.Section));
builder.Services.Configure<ExportOptions>(config.GetSection(ExportOptions.Section));
builder.Services.Configure<RetentionOptions>(config.GetSection(RetentionOptions.Section));

// Request limits and graceful shutdown: on SIGTERM, in-flight requests and workers get 30s to finish.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 10 * 1024 * 1024);
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

// ---------------------------------------------------------------------------------------------
// Database
// ---------------------------------------------------------------------------------------------
var connectionString = config.GetConnectionString("AuditDb")
    ?? throw new InvalidOperationException("ConnectionStrings:AuditDb is not configured.");
Database.ConfigureDapper();
builder.Services.AddSingleton(_ => Database.Create(connectionString));
builder.Services.AddSingleton<Migrator>();
builder.Services.AddSingleton<DbSeeder>();

// ---------------------------------------------------------------------------------------------
// Application services.
// Repositories and services are stateless (they only hold the thread-safe NpgsqlDataSource), so they are SINGLETONS.
// That also lets the background workers (singletons) use them without a scope.
// ITenantContext is SCOPED: it reads the current request's user.
// ---------------------------------------------------------------------------------------------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AuditMetrics>();
builder.Services.AddSingleton<WorkerHeartbeat>();

builder.Services.AddSingleton<TenantRepository>();
builder.Services.AddSingleton<EventRepository>();
builder.Services.AddSingleton<CheckpointRepository>();
builder.Services.AddSingleton<ExportRepository>();
builder.Services.AddSingleton<RetentionRepository>();
builder.Services.AddSingleton<SummaryRepository>();

builder.Services.AddSingleton<EventValidator>();
builder.Services.AddSingleton<CursorCodec>();
builder.Services.AddSingleton<SelfAuditService>();
builder.Services.AddSingleton<IngestionService>();
builder.Services.AddSingleton<EventQueryService>();
builder.Services.AddSingleton<IntegrityService>();
builder.Services.AddSingleton<ExportService>();
builder.Services.AddSingleton<ExportProcessor>();
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddSingleton<SummaryService>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, TenantContext>();

// Background workers (can be switched off per instance, e.g. to run "API only" and "workers only" processes).
if (config.GetValue("Export:WorkerEnabled", true)) builder.Services.AddHostedService<ExportWorker>();
if (config.GetValue("Retention:WorkerEnabled", true)) builder.Services.AddHostedService<RetentionWorker>();

// ---------------------------------------------------------------------------------------------
// Authentication / authorization
//   Producers : X-Api-Key                        -> policy "Producer"
//   Console   : X-Tenant-Id + X-Role (+X-User-Id) -> policies "Auditor" / "ComplianceAdmin" (stand-in for SSO claims)
// ---------------------------------------------------------------------------------------------
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>(AuthSchemes.ApiKey, null)
    .AddScheme<AuthenticationSchemeOptions, AuditorHeaderAuthHandler>(AuthSchemes.AuditorHeaders, null);

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Producer, p => p.AddAuthenticationSchemes(AuthSchemes.ApiKey).RequireClaim(ClaimNames.ProducerId))
    .AddPolicy(Policies.Auditor, p => p.AddAuthenticationSchemes(AuthSchemes.AuditorHeaders).RequireRole(Roles.Auditor))
    .AddPolicy(Policies.ComplianceAdmin, p => p.AddAuthenticationSchemes(AuthSchemes.AuditorHeaders).RequireRole(Roles.ComplianceAdmin));

// ---------------------------------------------------------------------------------------------
// CORS: only the console's origin(s) from configuration.
// ---------------------------------------------------------------------------------------------
var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .WithHeaders("Content-Type", "X-Tenant-Id", "X-Role", "X-User-Id", "Idempotency-Key", Problems.CorrelationHeader)
    .WithMethods("GET", "POST", "PUT")
    .WithExposedHeaders(Problems.CorrelationHeader, ExportsController.ChecksumHeader, "Location", "Content-Disposition")));

// ---------------------------------------------------------------------------------------------
// MVC controllers + error model
// ---------------------------------------------------------------------------------------------
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict;   // numbers are numbers (also in the OpenAPI types)
    })
    .ConfigureApiBehaviorOptions(o =>
    {
        // Model binding errors (malformed JSON, wrong types, bad enum values) use the same ProblemDetails shape as everything else.
        o.InvalidModelStateResponseFactory = ctx =>
        {
            var malformed = ctx.ModelState.Keys.Any(k => k.StartsWith('$')) || ctx.ModelState.ContainsKey("");
            var errors = ctx.ModelState
                .Where(e => e.Value!.Errors.Count > 0)
                .ToDictionary(
                    e => e.Key.TrimStart('$', '.') is { Length: > 0 } k ? k : "body",
                    e => e.Value!.Errors.Select(x => string.IsNullOrEmpty(x.ErrorMessage) ? "Invalid value." : x.ErrorMessage).ToArray());
            var problem = Problems.Create(ctx.HttpContext, 400,
                malformed ? "malformed_request" : "validation_failed",
                malformed ? "The request body is missing, not valid JSON, or has the wrong shape." : "One or more parameters are invalid.",
                errors);
            return new ObjectResult(problem) { StatusCode = 400, ContentTypes = { "application/problem+json" } };
        };
    });
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// ---------------------------------------------------------------------------------------------
// Health: liveness = "the process is up" (no dependencies); readiness = "can serve traffic" (DB + workers).
// ---------------------------------------------------------------------------------------------
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<WorkerHeartbeatHealthCheck>("workers", tags: ["ready"]);

// ---------------------------------------------------------------------------------------------
// OpenAPI document (/openapi/v1.json) + Swagger UI (/swagger). The React client's types are generated from it.
// ---------------------------------------------------------------------------------------------
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
builder.Services.AddOpenApi(o =>
{
    o.AddDocumentTransformer(OpenApiSetup.AddHeaderAuth);
    o.AddSchemaTransformer(OpenApiSetup.MarkNonNullablePropertiesRequired);
});

var app = builder.Build();

// ---------------------------------------------------------------------------------------------
// Migrate (and seed demo data) before accepting traffic.
// ---------------------------------------------------------------------------------------------
using (var startup = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
{
    await app.Services.GetRequiredService<Migrator>().MigrateAsync(startup.Token);
    if (config.GetValue<bool>("Database:SeedDemoData"))
        await app.Services.GetRequiredService<DbSeeder>().SeedAsync(startup.Token);
}

// ---------------------------------------------------------------------------------------------
// HTTP pipeline (order matters)
// ---------------------------------------------------------------------------------------------
app.UseMiddleware<CorrelationMiddleware>();     // 1. correlation id first, so every log line and error has it
app.UseExceptionHandler();                       // 2. exceptions -> ProblemDetails
app.UseStatusCodePages(async ctx =>              // 3. bare 404/405/415 -> ProblemDetails
{
    var http = ctx.HttpContext;
    var code = http.Response.StatusCode switch { 404 => "not_found", 405 => "method_not_allowed", 415 => "unsupported_media_type", _ => "error" };
    await Problems.WriteAsync(http, http.Response.StatusCode, code, "The requested resource or method is not available.");
});
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantLoggingMiddleware>();    // after auth: TenantId / ProducerId into the log scope

app.MapOpenApi();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/openapi/v1.json", "AuditTrail API v1");
    o.RoutePrefix = "swagger";
});

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.MapControllers();

app.Run();

/// <summary>Lets the integration tests start the app with WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
