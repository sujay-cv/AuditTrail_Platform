using AuditTrail.Api.Auth;
using AuditTrail.Api.Dtos;
using AuditTrail.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuditTrail.Api.Controllers;

[ApiController]
[Route("api/exports")]
[Authorize(Policy = Policies.Auditor)]
[Tags("Exports")]
public sealed class ExportsController(ExportService exports, ITenantContext tenant) : ControllerBase
{
    public const string ChecksumHeader = "X-Checksum-SHA256";

    /// <summary>Request an export (NDJSON). Requires an Idempotency-Key header.</summary>
    /// <remarks>
    /// 202 Accepted + Location of the job. Repeating the request with the same key and body returns the SAME job (also 202).
    /// Same key with a different body = 409.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<ExportJobDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, [FromBody] CreateExportRequest request, CancellationToken ct)
    {
        var (job, _) = await exports.CreateAsync(tenant.TenantId, tenant.UserId, idempotencyKey, request, ct);
        return AcceptedAtAction(nameof(Get), new { id = job.Id }, job);
    }

    /// <summary>The 50 most recent export jobs of the tenant.</summary>
    [HttpGet]
    public Task<List<ExportJobDto>> List(CancellationToken ct) => exports.ListAsync(tenant.TenantId, ct);

    /// <summary>Status and progress (rowCount, currentCursor) of one job.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ExportJobDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<ExportJobDto> Get(Guid id, CancellationToken ct) => exports.GetAsync(tenant.TenantId, id, ct);

    /// <summary>Cancel a Queued or Running job. 409 (with currentStatus) if it already finished.</summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<ExportJobDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ExportJobDto> Cancel(Guid id, CancellationToken ct) => exports.CancelAsync(tenant.TenantId, tenant.UserId, id, ct);

    /// <summary>Download a Completed export. The SHA-256 of the file is in the X-Checksum-SHA256 header.</summary>
    [HttpGet("{id:guid}/download")]
    [Produces("application/x-ndjson")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var (path, checksum, fileName) = await exports.GetDownloadAsync(tenant.TenantId, id, ct);
        Response.Headers[ChecksumHeader] = checksum;
        return PhysicalFile(path, "application/x-ndjson", fileName);   // streamed from disk, not loaded into memory
    }
}
