import { Link, useLocation, useParams } from 'react-router-dom'
import { useEvent } from '../api/queries'
import { actorName, actorTypeLabel, categoryHelp, humanizeAction, relativeTime } from '../lib/format'
import { CopyButton } from '../components/CopyButton'
import { ErrorPanel } from '../components/ErrorPanel'
import { MetadataView } from '../components/MetadataView'
import { CategoryBadge, Loading, OutcomeBadge } from '../components/Status'
import { Time } from '../components/Time'

export function EventDetailPage() {
  const { id = '' } = useParams()
  const location = useLocation()
  const back = (location.state as { back?: string } | null)?.back ?? '/events'
  const event = useEvent(id)

  return (
    <section aria-label="Activity details">
      <Link to={back} className="back-link">← Back to activity log</Link>

      {event.isPending && <Loading label="Loading activity…" />}
      {event.isError && <ErrorPanel error={event.error} />}
      {event.data && (() => {
        const e = event.data
        return (
          <>
            <div className="detail-hero card">
              <div>
                <p className="eyebrow">Activity record #{e.sequence.toLocaleString()}</p>
                <h1>{humanizeAction(e.action)}</h1>
                <p className="hero-sentence">
                  <strong>{actorName(e.actor.id)}</strong> · {e.resource.type} <strong>{e.resource.id}</strong> · {relativeTime(e.occurredAt)}
                </p>
              </div>
              <div className="hero-badges">
                <OutcomeBadge value={e.outcome} />
                <CategoryBadge value={e.category} />
              </div>
            </div>

            <div className="info-grid">
              <InfoCard title="Who" icon="👤">
                <Field label="Name">{actorName(e.actor.id)}</Field>
                <Field label="Type">{actorTypeLabel[e.actor.type] ?? e.actor.type}</Field>
                <Field label="Full ID"><code>{e.actor.id}</code></Field>
                {e.actor.ip && <Field label="IP address">{e.actor.ip}</Field>}
                {e.actor.userAgent && <Field label="Device / browser">{e.actor.userAgent}</Field>}
              </InfoCard>

              <InfoCard title="What" icon="📝">
                <Field label="Action">{humanizeAction(e.action)}</Field>
                <Field label="Item">{e.resource.type} <strong>{e.resource.id}</strong></Field>
                <Field label="Result"><OutcomeBadge value={e.outcome} /></Field>
                <Field label="Category">{categoryHelp[e.category]}</Field>
              </InfoCard>

              <InfoCard title="When" icon="🕒">
                <Field label="Happened"><Time iso={e.occurredAt} /></Field>
                <Field label="Recorded"><Time iso={e.receivedAt} /></Field>
                <p className="hint">“Happened” is the time reported by {e.producerName}. “Recorded” is when this platform saved it.</p>
              </InfoCard>

              <InfoCard title="Source" icon="🏢">
                <Field label="Reported by">{e.producerName}</Field>
                <Field label="Their reference"><code>{e.eventId}</code></Field>
              </InfoCard>
            </div>

            <div className="card">
              <h2>Additional details</h2>
              <MetadataView metadata={e.metadata} redactedKeys={e.redactedKeys} />
            </div>

            <details className="card technical">
              <summary>
                <strong>Proof of integrity</strong>{' '}
                <span className="muted small">— technical details used to prove this record was not changed</span>
              </summary>
              <p className="hint">
                Each record is sealed with a digital fingerprint that also includes the fingerprint of the record before it.
                Changing or deleting any record breaks every fingerprint after it, which the <Link to={`/integrity?to=${e.sequence}`}>tamper check</Link> detects.
              </p>
              <dl className="kv">
                <TechRow label="Record number" value={String(e.sequence)} />
                <TechRow label="Record ID" value={e.id} />
                <TechRow label="Source reference" value={e.eventId} />
                <TechRow label={`Fingerprint (SHA-256, v${e.hashVersion})`} value={e.hash} />
                <TechRow label="Previous record’s fingerprint" value={e.prevHash} />
              </dl>
            </details>
          </>
        )
      })()}
    </section>
  )
}

function InfoCard({ title, icon, children }: { title: string; icon: string; children: React.ReactNode }) {
  return (
    <div className="card info-card">
      <h2><span aria-hidden="true">{icon}</span> {title}</h2>
      <dl className="kv">{children}</dl>
    </div>
  )
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="kv-row">
      <dt>{label}</dt>
      <dd>{children}</dd>
    </div>
  )
}

function TechRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="kv-row">
      <dt>{label}</dt>
      <dd className="mono hash">{value} <CopyButton value={value} label={label} /></dd>
    </div>
  )
}
