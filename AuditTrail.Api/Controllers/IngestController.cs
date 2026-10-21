using System.Text.Json;
using AuditTrail.Api.Auth;
using AuditTrail.Api.Dtos;
using AuditTrail.Api.Infrastructure;
using AuditTrail.Api.Models;
using AuditTrail.Api.Options;
using AuditTrail.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AuditTrail.Api.Controllers;

/// <summary>
/// Producer endpoints (X-Api-Key). The tenant and producer come from the key, never from the request.
/// </summary>
[ApiController]
[Route("api/ingest")]
[Authorize(Policy = Policies.Producer)]
[Tags("Ingestion (producers)")]
public sealed class IngestController(IngestionService ingestion, ITenantContext tenant, IOptions<IngestionOptions> options) : ControllerBase
{
    /// <summary>Ingest one event.</summary>
    /// <remarks>
    /// 201 = stored (durable: the transaction has committed). 200 = duplicate (already stored; original sequence returned).
    /// 409 = same EventId with a different payload. 400 = validation failed.
    /// Body: { eventId, occurredAt, actor: { type, id, ip?, userAgent? }, action, resource: { type, id }, outcome, category, metadata? }
    /// </remarks>
    [HttpPost("events")]
    [ProducesResponseType<EventResultDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<EventResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> IngestOne([FromBody] JsonElement body, CancellationToken ct)
    {
        var result = (await ingestion.IngestAsync(tenant.TenantId, tenant.ProducerId, [body], ct))[0];

        return result.Status switch
        {
            IngestStatus.Accepted => StatusCode(StatusCodes.Status201Created, result),
            IngestStatus.Duplicate => Ok(result),
            IngestStatus.Conflict => throw ApiException.Conflict("event_id_conflict", result.Message!,
                new Dictionary<string, object?> { ["eventId"] = result.EventId, ["existingSequence"] = result.Sequence }),
            _ => throw ApiException.BadRequest(result.Reason!, result.Message!, result.Errors),
        };
    }

    /// <summary>Ingest up to 500 events. Valid events are stored even if others fail.</summary>
    /// <remarks>
    /// Returns a result per event (Accepted / Duplicate / Conflict / Rejected with reason and field errors).
    /// 200 when every event is Accepted or Duplicate (nothing for the caller to fix);
    /// 207 Multi-Status when at least one event was Rejected or Conflicted (the caller must read the per-event results).
    /// 400 for an empty batch, 413 for more than 500 events.
    /// </remarks>
    [HttpPost("events/batch")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    [ProducesResponseType<BatchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<BatchResponse>(StatusCodes.Status207MultiStatus)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> IngestBatch([FromBody] BatchRequest body, CancellationToken ct)
    {
        var max = options.Value.MaxBatchSize;
        if (body.Events is null) throw ApiException.Field("events", "required", "events is required.");
        if (body.Events.Count == 0) throw ApiException.Field("events", "empty_batch", "The batch must contain at least one event.");
        if (body.Events.Count > max)
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "batch_too_large", $"A batch may contain at most {max} events (got {body.Events.Count}).");

        var results = await ingestion.IngestAsync(tenant.TenantId, tenant.ProducerId, body.Events, ct);
        var response = new BatchResponse(
            results.Count(r => r.Status == IngestStatus.Accepted),
            results.Count(r => r.Status == IngestStatus.Duplicate),
            results.Count(r => r.Status == IngestStatus.Conflict),
            results.Count(r => r.Status == IngestStatus.Rejected),
            results);

        var allGood = response.Rejected == 0 && response.Conflicts == 0;
        return StatusCode(allGood ? StatusCodes.Status200OK : StatusCodes.Status207MultiStatus, response);
    }
}
