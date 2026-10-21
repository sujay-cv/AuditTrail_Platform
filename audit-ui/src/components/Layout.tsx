import { useQuery, useQueryClient } from '@tanstack/react-query'
import { NavLink, Outlet } from 'react-router-dom'
import { apiBaseUrl } from '../api/client'
import { useTenants } from '../api/queries'
import { useSession } from '../session/useSession'
import type { Role } from '../session/session'

const NAV = [
  { to: '/events', icon: '☰', label: 'Activity Log', hint: 'See who did what' },
  { to: '/overview', icon: '◔', label: 'Dashboard', hint: 'Summary and trends' },
  { to: '/exports', icon: '⬇', label: 'Exports', hint: 'Download records' },
  { to: '/integrity', icon: '🛡', label: 'Tamper Check', hint: 'Prove nothing changed' },
  { to: '/retention', icon: '🗄', label: 'Retention & Holds', hint: 'How long records are kept' },
]

/** Checks /health/ready every 5s so people see straight away when the service or its database is down. */
function ConnectionStatus() {
  const ready = useQuery({
    queryKey: ['health'],
    queryFn: async ({ signal }) => (await fetch(`${apiBaseUrl}/health/ready`, { signal })).ok,
    refetchInterval: 5000,
    retry: false,
  })
  const state = ready.isPending ? 'checking' : ready.data ? 'online' : 'offline'
  const text = { checking: 'Connecting…', online: 'Connected', offline: 'Service unavailable' }[state]
  return (
    <span className={`connection ${state}`} role="status" aria-live="polite" title={state === 'offline' ? 'The service or its database cannot be reached. Data may be out of date.' : undefined}>
      <span className="dot" aria-hidden="true" /> {text}
    </span>
  )
}

/** Organization + role picker: a demo stand-in for single sign-on. */
function IdentityPicker() {
  const [session, setSession] = useSession()
  const tenants = useTenants()
  const client = useQueryClient()

  function change(next: { tenantId?: string; role?: Role }) {
    // Forget everything loaded for the previous organization/role: nothing from tenant A stays in memory for tenant B.
    client.removeQueries({ predicate: (q) => q.queryKey[0] !== 'tenants' && q.queryKey[0] !== 'health' })
    setSession({ ...session, ...next })
  }

  return (
    <div className="identity" aria-label="Signed-in identity (demo stand-in for single sign-on)">
      <label>
        <span>Organization</span>
        <select value={session.tenantId} onChange={(e) => change({ tenantId: e.target.value })}>
          {(tenants.data ?? [{ id: session.tenantId, name: 'Loading…' }]).map((t) => (
            <option key={t.id} value={t.id}>{t.name}</option>
          ))}
        </select>
      </label>
      <label>
        <span>Signed in as</span>
        <select value={session.role} onChange={(e) => change({ role: e.target.value as Role })}>
          <option value="Auditor">Auditor (view only)</option>
          <option value="ComplianceAdmin">Compliance admin</option>
        </select>
      </label>
    </div>
  )
}

export function Layout() {
  const [session] = useSession()
  return (
    <div className="shell">
      <a href="#main" className="skip-link">Skip to content</a>
      <aside className="sidebar">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true">◆</span>
          <span>
            <strong>Audit Trail</strong>
            <span className="brand-sub">Compliance console</span>
          </span>
        </div>
        <nav aria-label="Main">
          {NAV.map((n) => (
            <NavLink key={n.to} to={n.to} className="nav-item">
              <span className="nav-icon" aria-hidden="true">{n.icon}</span>
              <span>
                <span className="nav-label">{n.label}</span>
                <span className="nav-hint">{n.hint}</span>
              </span>
            </NavLink>
          ))}
        </nav>
        <div className="sidebar-footer">
          <ConnectionStatus />
        </div>
      </aside>

      <div className="content">
        <header className="topbar">
          <IdentityPicker />
        </header>
        {/* key: remount pages when the identity changes, so no screen state from the old organization survives */}
        <main id="main" key={`${session.tenantId}-${session.role}`}>
          <Outlet />
        </main>
      </div>
    </div>
  )
}
