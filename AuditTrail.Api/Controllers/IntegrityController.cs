using AuditTrail.Api.Auth;
using AuditTrail.Api.Dtos;
using AuditTrail.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuditTrail.Api.Controllers;

[ApiController]
[Route("api/integrity")]
[Authorize(Policy = Policies.Auditor)]
[Tags("Integrity")]
public sealed class IntegrityController(IntegrityService integrity, ITenantContext tenant) : ControllerBase
{
    /// <summary>Verify the tenant's hash chain over a sequence range (both optional: default = whole chain).</summary>
    /// <remarks>Ok=false is a normal 200 result that reports the first broken sequence, not an error.</remarks>
    [HttpPost("verify")]
    [ProducesResponseType<VerifyResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<VerifyResultDto> Verify([FromBody] VerifyRequest request, CancellationToken ct)
        => integrity.VerifyAsync(tenant.TenantId, request.FromSequence, request.ToSequence, ct);
}
