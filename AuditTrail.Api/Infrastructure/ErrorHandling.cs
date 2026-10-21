using AuditTrail.Api.Auth;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;

namespace AuditTrail.Api.Infrastructure;

/// <summary>Builds every error response in one consistent RFC 7807 shape: type, title, status, detail, code, correlationId, errors.</summary>
public static class Problems
{
    public const string TypePrefix = "urn:audit-trail:error:";
    public const string CorrelationHeader = "X-Correlation-Id";

    public static string CorrelationId(HttpContext ctx) => ctx.TraceIdentifier;

    public static ProblemDetails Create(HttpContext ctx, int status, string code, string detail,
        IDictionary<string, string[]>? fieldErrors = null, IDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Type = TypePrefix + code,
            Title = ReasonPhrases.GetReasonPhrase(status),
            Detail = detail,
        };
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = CorrelationId(ctx);
        if (fieldErrors is { Count: > 0 }) problem.Extensions["errors"] = fieldErrors;
        if (extensions != null)
            foreach (var (key, value) in extensions) problem.Extensions[key] = value;
        return problem;
    }

    public static Task WriteAsync(HttpContext ctx, int status, string code, string detail,
        IDictionary<string, string[]>? fieldErrors = null, IDictionary<string, object?>? extensions = null)
    {
        ctx.Response.StatusCode = status;
        var problem = Create(ctx, status, code, detail, fieldErrors, extensions);
        return ctx.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", ctx.RequestAborted);
    }
}

/// <summary>
/// Reads X-Correlation-Id (or creates one), echoes it on the response and puts it in the logging scope,
/// so a support engineer can go from the id shown in the UI straight to the log lines.
/// Also writes ONE line per request (method, path without query string, status, duration): every reference code shown
/// in the console can be found in the logs, even for a simple 400. No headers, bodies, query values or IPs are logged.
/// </summary>
public sealed class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var id = ctx.Request.Headers[Problems.CorrelationHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            id = Guid.NewGuid().ToString("N");

        ctx.TraceIdentifier = id;
        ctx.Response.Headers[Problems.CorrelationHeader] = id;

        // Message-template scopes are structured (CorrelationId is a named property) AND readable in the console output.
        using (logger.BeginScope("CorrelationId:{CorrelationId}", id))
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                await next(ctx);
            }
            finally
            {
                if (!ctx.Request.Path.StartsWithSegments("/health"))   // health probes would flood the log
                    logger.LogInformation("{Method} {Path} -> {StatusCode} in {ElapsedMs:0} ms",
                        ctx.Request.Method, ctx.Request.Path.Value, ctx.Response.StatusCode,
                        System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }
}

/// <summary>After authentication: adds TenantId / ProducerId to the logging scope. Never logs metadata or IPs.</summary>
public sealed class TenantLoggingMiddleware(RequestDelegate next, ILogger<TenantLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var tenant = ctx.User.FindFirst(ClaimNames.TenantId)?.Value;
        if (tenant is null)
        {
            await next(ctx);
            return;
        }
        var producer = ctx.User.FindFirst(ClaimNames.ProducerId)?.Value ?? "-";

        using (logger.BeginScope("TenantId:{TenantId} ProducerId:{ProducerId}", tenant, producer))
            await next(ctx);
    }
}

/// <summary>
/// The one place where exceptions become HTTP responses. Never returns stack traces, SQL or connection strings.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        switch (ex)
        {
            case ApiException api:
                await Problems.WriteAsync(ctx, api.Status, api.Code, api.Message, api.FieldErrors, api.Extensions);
                return true;

            case OperationCanceledException when ctx.RequestAborted.IsCancellationRequested:
                // The client went away. The transaction was rolled back; nothing to write. 499 = "client closed request".
                ctx.Response.StatusCode = 499;
                return true;

            case BadHttpRequestException bad when bad.StatusCode == StatusCodes.Status413PayloadTooLarge:
                await Problems.WriteAsync(ctx, 413, "payload_too_large", "The request body is too large.");
                return true;

            case BadHttpRequestException:
                await Problems.WriteAsync(ctx, 400, "malformed_request", "The request is malformed.");
                return true;

            case NpgsqlException npg when IsTransient(npg):
            case TimeoutException:
                // Log the type only: Npgsql messages can contain host names.
                logger.LogWarning("Database unavailable or timed out ({ErrorType})", ex.GetType().Name);
                ctx.Response.Headers.RetryAfter = "2";
                await Problems.WriteAsync(ctx, 503, "service_unavailable", "The service is temporarily unavailable. Please retry shortly.");
                return true;

            default:
                logger.LogError(ex, "Unhandled exception");
                await Problems.WriteAsync(ctx, 500, "internal_error", "An unexpected error occurred. Quote the correlation id when contacting support.");
                return true;
        }
    }

    /// <summary>
    /// "Try again later" database conditions: connection failures, lock_timeout (55P03), statement timeout (57014),
    /// too many connections (53300), server shutting down (57P01/57P03), connection exceptions (08xxx).
    /// </summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        PostgresException pg => pg.SqlState is "55P03" or "57014" or "53300" or "57P01" or "57P03" || pg.SqlState.StartsWith("08", StringComparison.Ordinal),
        NpgsqlException => true,      // socket / connect / protocol failure
        TimeoutException => true,
        _ => false,
    };
}
