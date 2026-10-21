using AuditTrail.Api.Auth;
using AuditTrail.Api.Dtos;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;
using AuditTrail.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuditTrail.Api.Controllers;

/// <summary>Auditors can read. Only ComplianceAdmin can change (an Auditor gets 403).</summary>
[ApiController]
[Route("api/retention")]
[Authorize(Policy = Policies.Auditor)]
[Tags("Retention & legal holds")]
public sealed class RetentionController(RetentionService retention, IntegrityService integrity, ITenantContext tenant) : ControllerBase
{
    [HttpGet("policies")]
    public Task<List<RetentionPolicyDto>> ListPolicies(CancellationToken ct) => retention.ListPoliciesAsync(tenant.TenantId, ct);

    /// <summary>Set the retention days for one category.</summary>
    [HttpPut("policies/{category}")]
    [Authorize(Policy = Policies.ComplianceAdmin)]
    [ProducesResponseType<RetentionPolicyDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public Task<RetentionPolicyDto> UpdatePolicy(Category category, [FromBody] UpdateRetentionRequest request, CancellationToken ct)
        => retention.UpdatePolicyAsync(tenant.TenantId, tenant.UserId, category, request.RetentionDays, ct);

    /// <summary>Run retention now for this tenant (the worker also runs it on a schedule).</summary>
    /// <remarks>Verifies the chain and writes a checkpoint first; if the chain is broken nothing is deleted (409).</remarks>
    [HttpPost("run")]
    [Authorize(Policy = Policies.ComplianceAdmin)]
    [ProducesResponseType<RetentionRunResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<RetentionRunResultDto> Run(CancellationToken ct)
    {
        if (!await integrity.CreateCheckpointAsync(tenant.TenantId, ct))
            throw ApiException.Conflict("chain_broken", "The hash chain failed verification, so retention will not delete anything. Run an integrity check.");
        return new RetentionRunResultDto(await retention.PurgeTenantAsync(tenant.TenantId, tenant.UserId, ct));
    }
}

[ApiController]
[Route("api/legal-holds")]
[Authorize(Policy = Policies.Auditor)]
[Tags("Retention & legal holds")]
public sealed class LegalHoldsController(RetentionService retention, ITenantContext tenant) : ControllerBase
{
    [HttpGet]
    public Task<List<LegalHoldDto>> List(CancellationToken ct) => retention.ListHoldsAsync(tenant.TenantId, ct);

    /// <summary>Create a legal hold. scopeType: Actor (actor id), Resource ("Type/Id") or Category (category name). Reason is mandatory.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ComplianceAdmin)]
    [ProducesResponseType<LegalHoldDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateLegalHoldRequest request, CancellationToken ct)
    {
        var hold = await retention.CreateHoldAsync(tenant.TenantId, tenant.UserId, request, ct);
        return StatusCode(StatusCodes.Status201Created, hold);
    }

    /// <summary>Release an active hold (reason mandatory). 409 if already released.</summary>
    [HttpPost("{id:guid}/release")]
    [Authorize(Policy = Policies.ComplianceAdmin)]
    [ProducesResponseType<LegalHoldDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<LegalHoldDto> Release(Guid id, [FromBody] ReleaseLegalHoldRequest request, CancellationToken ct)
        => retention.ReleaseHoldAsync(tenant.TenantId, tenant.UserId, id, request, ct);
}
