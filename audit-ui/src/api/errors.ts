/**
 * Every failure the UI shows goes through ApiError, built from the API's RFC 7807 ProblemDetails.
 * We never show raw response bodies or stack traces: only `title`/`detail`, field errors, and the correlation id.
 */
export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly correlationId: string | null
  readonly fieldErrors: Record<string, string[]>
  readonly extensions: Record<string, unknown>

  constructor(init: {
    status: number
    code: string
    message: string
    correlationId: string | null
    fieldErrors?: Record<string, string[]>
    extensions?: Record<string, unknown>
  }) {
    super(init.message)
    this.name = 'ApiError'
    this.status = init.status
    this.code = init.code
    this.correlationId = init.correlationId
    this.fieldErrors = init.fieldErrors ?? {}
    this.extensions = init.extensions ?? {}
  }

  /** First error for a form field, if the API reported one. */
  field(name: string): string | undefined {
    return this.fieldErrors[name]?.[0]
  }
}

/** Plain-language text per status, used when the API did not send its own explanation. */
const fallbackMessages: Record<number, string> = {
  0: 'We can’t reach the service. Check your connection and try again.',
  400: 'Some of the information entered isn’t valid.',
  401: 'You are not signed in to this organization.',
  403: 'Your role doesn’t allow this action.',
  404: 'We couldn’t find this. It may not exist, or it belongs to a different organization.',
  409: 'This can’t be done in the current state.',
  413: 'This request is too large.',
  500: 'Something went wrong on our side. Please try again in a moment.',
  503: 'The service is temporarily unavailable. Please try again in a moment.',
}

const KNOWN = new Set(['type', 'title', 'status', 'detail', 'instance', 'code', 'correlationId', 'errors', 'traceId'])

/** Maps a ProblemDetails body (or anything else) + the response to an ApiError. Pure: easy to unit test. */
export function toApiError(status: number, body: unknown, correlationHeader: string | null): ApiError {
  const problem = (typeof body === 'object' && body !== null ? body : {}) as Record<string, unknown>
  const str = (v: unknown) => (typeof v === 'string' && v.length > 0 ? v : undefined)

  const fieldErrors: Record<string, string[]> = {}
  if (typeof problem.errors === 'object' && problem.errors !== null) {
    for (const [key, value] of Object.entries(problem.errors as Record<string, unknown>)) {
      if (Array.isArray(value)) fieldErrors[key] = value.filter((v): v is string => typeof v === 'string')
    }
  }

  const extensions: Record<string, unknown> = {}
  for (const [k, v] of Object.entries(problem)) if (!KNOWN.has(k)) extensions[k] = v

  return new ApiError({
    status,
    code: str(problem.code) ?? (status === 0 ? 'network_error' : `http_${status}`),
    message: str(problem.detail) ?? fallbackMessages[status] ?? fallbackMessages[status >= 500 ? 500 : 400],
    correlationId: str(problem.correlationId) ?? correlationHeader,
    fieldErrors,
    extensions,
  })
}

export function networkError(correlationId: string | null): ApiError {
  return toApiError(0, null, correlationId)
}
