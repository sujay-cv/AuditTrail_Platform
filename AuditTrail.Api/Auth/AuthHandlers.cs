using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;
using AuditTrail.Api.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AuditTrail.Api.Auth;

public static class AuthSchemes
{
    public const string ApiKey = "ApiKey";                 // producers
    public const string AuditorHeaders = "AuditorHeaders"; // auditors / compliance admins
}

public static class Policies
{
    public const string Producer = "Producer";
    public const string Auditor = "Auditor";
    public const string ComplianceAdmin = "ComplianceAdmin";
}

public static class ClaimNames
{
    public const string TenantId = "tenant_id";
    public const string ProducerId = "producer_id";
    public const string UserId = "user_id";
}

public static class ApiKeyHasher
{
    /// <summary>API keys are long random strings, so plain SHA-256 is enough (no slow password hash needed). Only the hash is stored.</summary>
    public static string Hash(string apiKey) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
}

/// <summary>Common part of both handlers: 401 and 403 are returned as ProblemDetails, not as empty responses.</summary>
public abstract class ProblemAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private string? _failure;

    protected AuthenticateResult Fail(string message)
    {
        _failure = message;
        return AuthenticateResult.Fail(message);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        => Problems.WriteAsync(Context, 401, "unauthenticated", _failure ?? "Authentication is required.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        => Problems.WriteAsync(Context, 403, "forbidden", "Your role is not allowed to perform this action.");
}

/// <summary>
/// Producers: X-Api-Key -> hash -> producers row -> (tenant, producer).
/// The tenant comes ONLY from the key, so a producer cannot write into another tenant (there is no tenant field to fake).
/// Unknown or revoked keys get 401.
/// </summary>
public sealed class ApiKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, TenantRepository tenants)
    : ProblemAuthHandler(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var apiKey = Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrWhiteSpace(apiKey)) return Fail("The X-Api-Key header is required.");
        if (apiKey.Length > 200) return Fail("Invalid or revoked API key.");

        var producer = await tenants.FindProducerByKeyHashAsync(ApiKeyHasher.Hash(apiKey), Context.RequestAborted);
        if (producer is not { Status: "Active" }) return Fail("Invalid or revoked API key.");

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimNames.TenantId, producer.TenantId.ToString()),
            new Claim(ClaimNames.ProducerId, producer.Id.ToString()),
            new Claim(ClaimNames.UserId, producer.Name),
        ], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

/// <summary>
/// Auditors: STAND-IN for a real SSO token. Reads X-Tenant-Id, X-Role and optional X-User-Id and turns them into the
/// same claims a validated JWT would contain. In production you replace this handler with AddJwtBearer(...) and
/// map the token's tenant/role claims; controllers and services do not change.
/// ComplianceAdmin also gets the Auditor role, because an admin can do everything an auditor can.
/// </summary>
public sealed class AuditorHeaderAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, TenantRepository tenants)
    : ProblemAuthHandler(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Guid.TryParse(Request.Headers["X-Tenant-Id"], out var tenantId))
            return Fail("A valid X-Tenant-Id header is required.");

        var role = Request.Headers["X-Role"].ToString();
        if (role is not (Roles.Auditor or Roles.ComplianceAdmin))
            return Fail("X-Role must be 'Auditor' or 'ComplianceAdmin'.");

        if (!await tenants.ExistsAsync(tenantId, Context.RequestAborted))
            return Fail("Unknown tenant.");

        var userId = Request.Headers["X-User-Id"].ToString();
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 100) userId = role.ToLowerInvariant() + "@console";

        var claims = new List<Claim>
        {
            new(ClaimNames.TenantId, tenantId.ToString()),
            new(ClaimNames.UserId, userId),
            new(ClaimTypes.Role, Roles.Auditor),
        };
        if (role == Roles.ComplianceAdmin) claims.Add(new Claim(ClaimTypes.Role, Roles.ComplianceAdmin));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
