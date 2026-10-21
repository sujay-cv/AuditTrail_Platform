using System.Security.Claims;

namespace AuditTrail.Api.Auth;

/// <summary>
/// Tenant isolation, central rule: the tenant id ALWAYS comes from the authenticated identity (claims), never from a
/// route, query string or body. Controllers read it from here and pass it to services; every repository query filters by it.
/// </summary>
public interface ITenantContext
{
    Guid TenantId { get; }
    /// <summary>Who is acting: the auditor's user id, or the producer's name.</summary>
    string UserId { get; }
    /// <summary>Only set for producer requests.</summary>
    Guid ProducerId { get; }
}

public sealed class TenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    private ClaimsPrincipal User => accessor.HttpContext?.User
        ?? throw new InvalidOperationException("TenantContext used outside an HTTP request.");

    public Guid TenantId => Guid.Parse(User.FindFirstValue(ClaimNames.TenantId)
        ?? throw new InvalidOperationException("Request is not authenticated."));

    public string UserId => User.FindFirstValue(ClaimNames.UserId) ?? "unknown";

    public Guid ProducerId => Guid.Parse(User.FindFirstValue(ClaimNames.ProducerId)
        ?? throw new InvalidOperationException("Request is not from a producer."));
}
