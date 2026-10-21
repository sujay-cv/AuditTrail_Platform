namespace AuditTrail.Api.Infrastructure;

/// <summary>
/// Throw this from anywhere to return an RFC 7807 ProblemDetails response.
/// <see cref="Code"/> is the stable, machine-readable error type (e.g. "range_too_large").
/// </summary>
public sealed class ApiException(
    int status, string code, string detail,
    IDictionary<string, string[]>? fieldErrors = null,
    IDictionary<string, object?>? extensions = null) : Exception(detail)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IDictionary<string, string[]>? FieldErrors { get; } = fieldErrors;
    public IDictionary<string, object?>? Extensions { get; } = extensions;

    public static ApiException BadRequest(string code, string detail, IDictionary<string, string[]>? fieldErrors = null)
        => new(StatusCodes.Status400BadRequest, code, detail, fieldErrors);

    public static ApiException Field(string field, string code, string message)
        => new(StatusCodes.Status400BadRequest, code, message, new Dictionary<string, string[]> { [field] = [message] });

    public static ApiException NotFound(string what)
        => new(StatusCodes.Status404NotFound, "not_found", $"{what} was not found.");

    public static ApiException Conflict(string code, string detail, IDictionary<string, object?>? extensions = null)
        => new(StatusCodes.Status409Conflict, code, detail, extensions: extensions);
}
