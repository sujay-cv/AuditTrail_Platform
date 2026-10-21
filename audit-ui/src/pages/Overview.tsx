import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { useProducers, useSummary } from '../api/queries'
import { CATEGORIES, OUTCOMES, type Outcome } from '../api/types'
import { filtersToParams } from '../lib/filters'
import { actorName, categoryHelp, categoryLabel, exportStatusLabel, humanSeconds, outcomeLabel, relativeTime } from '../lib/format'
import { ErrorPanel } from '../components/ErrorPanel'
import { Loading, PageHeader } from '../components/Status'

const RANGES = [
  { days: 7, label: '7 days' },
  { days: 30, label: '30 days' },
  { days: 90, label: '90 days' },
  { days: 365, label: '1 year' },
]

export function OverviewPage() {
  const [days, setDays] = useState(30)
  // Rounded to the minute so the cache key stays stable while you are on the page.
  const { from, to } = useMemo(() => {
    const end = new Date(Math.ceil(Date.now() / 60_000) * 60_000 + 60_000)
    return { from: new Date(end.getTime() - days * 86_400_000).toISOString(), to: end.toISOString() }
  }, [days])
  const summary = useSummary(from, to)
  const producers = useProducers()
  const platform = producers.data?.find((p) => p.name === 'audit-platform')

  // The Activity log searches at most 90 days, so drill-down links use the last 90 days of the selected period.
  const logFrom = new Date(Math.max(Date.parse(from), Date.parse(to) - 90 * 86_400_000)).toISOString()
  const logLink = (extra: Record<string, string>) => `/events?${filtersToParams({ from: logFrom, to, ...extra })}`

  return (
    <section aria-label="Dashboard">
      <PageHeader
        title="Dashboard"
        subtitle="A summary of recorded activity in your organization."
        actions={
          <div className="segmented" role="group" aria-label="Time period">
            {RANGES.map((r) => (
              <button key={r.days} type="button" aria-pressed={days === r.days} onClick={() => setDays(r.days)}>{r.label}</button>
            ))}
          </div>
        }
      />

      {summary.isPending && <Loading label="Loading summary…" />}
      {summary.isError && <ErrorPanel error={summary.error} onRetry={() => summary.refetch()} />}
      {summary.data && (() => {
        const s = summary.data
        const count = (c: string, o: Outcome) => s.counts.find((x) => x.category === c && x.outcome === o)?.count ?? 0
        const denied = s.counts.filter((c) => c.outcome === 'Denied').reduce((n, c) => n + c.count, 0)
        const maxCategory = Math.max(1, ...CATEGORIES.map((c) => OUTCOMES.reduce((n, o) => n + count(c, o), 0)))
        const maxDenied = Math.max(1, ...s.topDeniedActors.map((a) => a.count))
        const activeExports = s.exportsByStatus.filter((e) => e.status === 'Queued' || e.status === 'Running').reduce((n, e) => n + e.count, 0)

        return (
          <>
            <div className="stat-grid">
              <Stat label="Activities recorded" value={s.totalEvents.toLocaleString()} sub={`in the last ${RANGES.find((r) => r.days === days)?.label}`} />
              <Stat label="Denied attempts" value={denied.toLocaleString()} sub={<Link to={logLink({ outcome: 'Denied' })}>See who was denied →</Link>} tone={denied > 0 ? 'bad' : undefined} />
              <Stat label="Typical recording delay" value={humanSeconds(s.ingestionLag.p50Seconds)} sub={`slowest 5%: ${humanSeconds(s.ingestionLag.p95Seconds)}`} />
              <Stat label="Active legal holds" value={String(s.activeLegalHolds)} sub={<Link to="/retention">Manage holds →</Link>} />
              <Stat
                label="Last tamper check"
                value={s.latestCheckpoint ? 'Passed' : 'Not run yet'}
                tone={s.latestCheckpoint ? 'good' : undefined}
                sub={s.latestCheckpoint ? `up to record #${s.latestCheckpoint.sequence.toLocaleString()} · ${relativeTime(s.latestCheckpoint.createdAt)}` : <Link to="/integrity">Run a tamper check →</Link>}
              />
              <Stat label="Exports in progress" value={String(activeExports)} sub={<Link to="/exports">View exports →</Link>} />
            </div>

            <div className="two-columns">
              <div className="card">
                <h2>Activity by category</h2>
                <div className="legend small" aria-hidden="true">
                  {OUTCOMES.map((o) => <span key={o}><span className={`swatch outcome-${o.toLowerCase()}`} /> {outcomeLabel[o]}</span>)}
                </div>
                <ul className="bars">
                  {CATEGORIES.map((c) => {
                    const total = OUTCOMES.reduce((n, o) => n + count(c, o), 0)
                    return (
                      <li key={c}>
                        <div className="bar-label">
                          <Link to={logLink({ category: c })} title={categoryHelp[c]}>{categoryLabel[c]}</Link>
                          <span className="muted small">{total.toLocaleString()}</span>
                        </div>
                        <div className="bar-track" role="img"
                          aria-label={`${categoryLabel[c]}: ${OUTCOMES.map((o) => `${count(c, o)} ${outcomeLabel[o].toLowerCase()}`).join(', ')}`}>
                          {OUTCOMES.map((o) => (
                            <span key={o} className={`bar-seg outcome-${o.toLowerCase()}`} style={{ width: `${(count(c, o) / maxCategory) * 100}%` }} />
                          ))}
                        </div>
                      </li>
                    )
                  })}
                </ul>
              </div>

              <div className="card">
                <h2>Most denied attempts</h2>
                <p className="hint">People or systems that were refused access most often. Worth a look.</p>
                {s.topDeniedActors.length === 0 ? (
                  <p className="muted">No denied attempts in this period. 🎉</p>
                ) : (
                  <ol className="bars ranked">
                    {s.topDeniedActors.map((a) => (
                      <li key={a.actorId}>
                        <div className="bar-label">
                          <Link to={logLink({ actorId: a.actorId, outcome: 'Denied' })}>{actorName(a.actorId)}</Link>
                          <span className="muted small">{a.count.toLocaleString()} denied</span>
                        </div>
                        <div className="bar-track"><span className="bar-seg outcome-denied" style={{ width: `${(a.count / maxDenied) * 100}%` }} /></div>
                      </li>
                    ))}
                  </ol>
                )}
              </div>
            </div>

            <div className="card">
              <h2>Exports</h2>
              {s.exportsByStatus.length === 0 ? <p className="muted">No exports have been created.</p> : (
                <div className="chips">
                  {s.exportsByStatus.map((e) => (
                    <span key={e.status} className={`pill status-${e.status.toLowerCase()}`}>{exportStatusLabel[e.status]}: {e.count}</span>
                  ))}
                </div>
              )}
              {platform && (
                <p className="small">
                  <Link to={logLink({ producerId: platform.id })}>See the platform’s own records</Link>
                  <span className="muted"> — every export, legal hold and retention change is itself recorded in the audit trail.</span>
                </p>
              )}
            </div>
          </>
        )
      })()}
    </section>
  )
}

function Stat({ label, value, sub, tone }: { label: string; value: string; sub?: React.ReactNode; tone?: 'good' | 'bad' }) {
  return (
    <div className={`card stat ${tone ? `stat-${tone}` : ''}`}>
      <span className="stat-label">{label}</span>
      <span className="stat-value">{value}</span>
      {sub && <span className="small muted">{sub}</span>}
    </div>
  )
}
