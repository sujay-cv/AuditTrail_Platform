using AuditTrail.Api.Auth;
using AuditTrail.Api.Dtos;
using AuditTrail.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuditTrail.Api.Controllers;

[ApiController]
[Route("api/events")]
[Authorize(Policy = Policies.Auditor)]
[Tags("Events")]
public sealed class EventsController(EventQueryService queries, ITenantContext tenant) : ControllerBase
{
    /// <summary>Search events (cursor paginated).</summary>
    /// <remarks>
    /// from/to (OccurredAt, UTC) are required, max 90 days, 'to' exclusive. Ordered by OccurredAt, then Sequence.
    /// Pass nextCursor back as 'cursor' (with the SAME filters) to get the next page. limit: 1-200, default 50.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<SearchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<SearchResponse> Search([FromQuery] SearchQuery query, CancellationToken ct)
        => queries.SearchAsync(tenant.TenantId, query, ct);

    /// <summary>Get one event, including its sequence and hashes. 404 if it does not exist in YOUR tenant.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<EventDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<EventDto> Get(Guid id, CancellationToken ct)
        => queries.GetAsync(tenant.TenantId, id, ct);
}
