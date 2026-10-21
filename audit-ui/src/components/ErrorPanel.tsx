import { ApiError } from '../api/errors'
import { humanizeKey } from '../lib/format'
import { CopyButton } from './CopyButton'

/**
 * Friendly error box. Shows what went wrong in plain words, which fields need fixing,
 * and a reference code (the correlation id) that support can use to find the exact log line.
 * Never shows stack traces or raw server output.
 */
export function ErrorPanel({ error, title, onRetry }: { error: unknown; title?: string; onRetry?: () => void }) {
  const apiError = error instanceof ApiError ? error : null
  const status = apiError?.status
  const heading =
    title ??
    (status === 403 ? 'Not allowed'
      : status === 404 ? 'Not found'
      : status === 0 || status === 503 ? 'The service is unavailable right now'
      : status === 409 ? 'This can’t be done right now'
      : status !== undefined && status < 500 ? 'Please check your input'
      : 'Something went wrong')
  const fields = Object.entries(apiError?.fieldErrors ?? {})

  return (
    <div className="alert alert-error" role="alert">
      <div className="alert-icon" aria-hidden="true">!</div>
      <div className="alert-body">
        <strong>{heading}</strong>
        <p>{apiError?.message ?? 'Something went wrong. Please try again.'}</p>
        {fields.length > 0 && (
          <ul>
            {fields.map(([field, messages]) => (
              <li key={field}>
                <strong>{humanizeKey(field)}:</strong> {messages.join(' ')}
              </li>
            ))}
          </ul>
        )}
        <div className="alert-footer">
          {onRetry && (
            <button type="button" onClick={onRetry}>
              Try again
            </button>
          )}
          {apiError?.correlationId && (
            <span className="muted small">
              Reference code for support: <code>{apiError.correlationId}</code>{' '}
              <CopyButton value={apiError.correlationId} label="reference code" />
            </span>
          )}
        </div>
      </div>
    </div>
  )
}

/** Small inline message under one form field. */
export function FieldError({ error, field }: { error: unknown; field: string }) {
  const message = error instanceof ApiError ? error.field(field) : undefined
  return message ? (
    <span className="field-error" role="alert">
      {message}
    </span>
  ) : null
}
