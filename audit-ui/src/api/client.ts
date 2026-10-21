import createClient, { type Middleware } from 'openapi-fetch'
import type { paths } from './schema'
import { ApiError, networkError, toApiError } from './errors'
import { getSession } from '../session/session'

declare global {
  interface Window {
    __APP_CONFIG__?: { apiBaseUrl?: string }
  }
}

export const apiBaseUrl = (window.__APP_CONFIG__?.apiBaseUrl ?? 'http://localhost:8080').replace(/\/$/, '')

/**
 * Adds identity headers to every request.
 * X-Tenant-Id / X-Role are the demo stand-in for SSO: in production the browser would hold an HTTP-only session cookie
 * (via a BFF) or an in-memory access token, and tenant + role would be claims inside the token.
 * A client-generated X-Correlation-Id means even a request that never reached the server has an id to quote.
 */
const identity: Middleware = {
  onRequest({ request }) {
    const session = getSession()
    request.headers.set('X-Tenant-Id', session.tenantId)
    request.headers.set('X-Role', session.role)
    request.headers.set('X-User-Id', session.role === 'ComplianceAdmin' ? 'admin@console' : 'auditor@console')
    if (!request.headers.has('X-Correlation-Id')) request.headers.set('X-Correlation-Id', crypto.randomUUID())
    return request
  },
}

/** The typed client: paths, parameters and bodies are all checked against the generated OpenAPI types. */
export const api = createClient<paths>({ baseUrl: apiBaseUrl, fetch: (request) => globalThis.fetch(request) })
api.use(identity)

type Result<T> = { data?: T; error?: unknown; response: Response }

/**
 * Turns an openapi-fetch result into data or a thrown ApiError.
 * Usage: const events = await call(api.GET('/api/events', { params: { query }, signal }))
 */
export async function call<T>(promise: Promise<Result<T>>): Promise<T> {
  let result: Result<T>
  try {
    result = await promise
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') throw e   // superseded request: not an error
    throw networkError(null)
  }
  const { data, error, response } = result
  if (!response.ok) throw toApiError(response.status, error, response.headers.get('X-Correlation-Id'))
  return data as T
}

/** Downloads an export with auth headers, returning the file and the server's checksum header. */
export async function downloadExport(id: string): Promise<{ blob: Blob; checksum: string | null; fileName: string }> {
  const session = getSession()
  const correlationId = crypto.randomUUID()
  let response: Response
  try {
    response = await fetch(`${apiBaseUrl}/api/exports/${id}/download`, {
      headers: { 'X-Tenant-Id': session.tenantId, 'X-Role': session.role, 'X-Correlation-Id': correlationId },
    })
  } catch {
    throw networkError(correlationId)
  }
  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw toApiError(response.status, body, response.headers.get('X-Correlation-Id') ?? correlationId)
  }
  return { blob: await response.blob(), checksum: response.headers.get('X-Checksum-SHA256'), fileName: `audit-export-${id}.ndjson` }
}

export { ApiError }
