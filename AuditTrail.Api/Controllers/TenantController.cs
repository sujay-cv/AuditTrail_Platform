using AuditTrail.Api.Auth;
using AuditTrail.Api.Dtos;
using AuditTrail.Api.Repositories;
using AuditTrail.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuditTrail.Api.Controllers;

[ApiController]
[Route("api")]
[Tags("Tenant")]
public sealed class TenantController(SummaryService summaries, TenantRepository tenants, ITenantContext tenant, IConfiguration config) : ControllerBase
{
    /// <summary>Tenant summary for an OccurredAt range (max 366 days). All aggregation runs in PostgreSQL.</summary>
    [HttpGet("summary")]
    [Authorize(Policy = Policies.Auditor)]
    [ProducesResponseType<SummaryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<SummaryDto> Summary([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct)
        => summaries.GetAsync(tenant.TenantId, from, to, ct);

    /// <summary>Producers of the caller's tenant (for the producer filter).</summary>
    [HttpGet("producers")]
    [Authorize(Policy = Policies.Auditor)]
    public async Task<List<ProducerDto>> Producers(CancellationToken ct)
        => (await tenants.ListProducersAsync(tenant.TenantId, ct)).Select(p => new ProducerDto(p.Id, p.Name, p.Status)).ToList();

    /// <summary>DEMO ONLY: list of tenants for the console's tenant picker (stand-in for SSO). Disabled unless Demo:TenantDirectory=true.</summary>
    [HttpGet("tenants")]
    [AllowAnonymous]
    public async Task<ActionResult<List<TenantDto>>> Tenants(CancellationToken ct)
    {
        if (!config.GetValue<bool>("Demo:TenantDirectory")) return NotFound();
        return (await tenants.ListAsync(ct)).Select(t => new TenantDto(t.Id, t.Name)).ToList();
    }
}
