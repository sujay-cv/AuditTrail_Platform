/**
 * Who the console is acting as: tenant + role. A stand-in for SSO.
 *
 * Stored in sessionStorage (cleared when the tab closes) so a page reload keeps the selected tenant.
 * These are NOT credentials: the API trusts them only because this is a demo. With real SSO the token would live
 * in an HTTP-only cookie set by a BFF (or in memory with silent refresh) and tenant/role would be claims in it,
 * so nothing secret would ever be stored in localStorage / sessionStorage.
 */
export type Role = 'Auditor' | 'ComplianceAdmin'
export interface Session {
  tenantId: string
  role: Role
}

const KEY = 'audit-console-session'
const DEFAULT: Session = { tenantId: '11111111-1111-1111-1111-111111111111', role: 'Auditor' }

let current: Session = load()
const listeners = new Set<() => void>()

function load(): Session {
  try {
    const raw = sessionStorage.getItem(KEY)
    const parsed = raw ? (JSON.parse(raw) as Partial<Session>) : null
    if (parsed?.tenantId && (parsed.role === 'Auditor' || parsed.role === 'ComplianceAdmin'))
      return { tenantId: parsed.tenantId, role: parsed.role }
  } catch {
    /* ignore corrupt value */
  }
  return DEFAULT
}

export function getSession(): Session {
  return current
}

export function setSession(next: Session) {
  current = next
  sessionStorage.setItem(KEY, JSON.stringify(next))
  listeners.forEach((l) => l())
}

export function subscribe(listener: () => void) {
  listeners.add(listener)
  return () => listeners.delete(listener)
}
