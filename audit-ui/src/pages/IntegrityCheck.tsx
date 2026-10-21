import { useEffect, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useVerify } from '../api/queries'
import { breakReason, relativeTime } from '../lib/format'
import { ErrorPanel, FieldError } from '../components/ErrorPanel'
import { PageHeader } from '../components/Status'

export function IntegrityPage() {
  const [params] = useSearchParams()
  const [from, setFrom] = useState(params.get('from') ?? '')
  const [to, setTo] = useState(params.get('to') ?? '')
  const [showRange, setShowRange] = useState(Boolean(params.get('from') || params.get('to')))
  const verify = useVerify()
  const elapsed = useElapsedSeconds(verify.isPending)

  function run(e?: FormEvent) {
    e?.preventDefault()
    verify.mutate(showRange
      ? { fromSequence: from ? Number(from) : undefined, toSequence: to ? Number(to) : undefined }
      : {})
  }

  const r = verify.data
  return (
    <section aria-label="Tamper check">
      <PageHeader
        title="Tamper check"
        subtitle="Confirm that no activity record has been changed, deleted or reordered since it was saved."
      />

      <div className="card how-it-works">
        <div className="steps">
          <div><span className="step">1</span> Every record gets a unique digital fingerprint when it is saved.</div>
          <div><span className="step">2</span> Each fingerprint also includes the fingerprint of the record before it, forming a chain.</div>
          <div><span className="step">3</span> If anyone alters a record, its fingerprint no longer matches and the chain breaks at that exact record.</div>
        </div>

        <form onSubmit={run} aria-label="Run tamper check">
          <button type="button" className="link-button" aria-expanded={showRange} onClick={() => setShowRange((v) => !v)}>
            {showRange ? '− Check the whole history instead' : '+ Check only part of the history'}
          </button>
          {showRange && (
            <div className="filter-row">
              <label>
                From record #
                <input type="number" min={1} value={from} onChange={(e) => setFrom(e.target.value)} placeholder="first" />
                <FieldError error={verify.error} field="fromSequence" />
              </label>
              <label>
                To record #
                <input type="number" min={1} value={to} onChange={(e) => setTo(e.target.value)} placeholder="latest" />
                <FieldError error={verify.error} field="toSequence" />
              </label>
            </div>
          )}
          <button type="submit" className="primary big-button" disabled={verify.isPending}>
            🛡 {verify.isPending ? `Checking… ${elapsed}s` : 'Run tamper check'}
          </button>
        </form>
      </div>

      {verify.isPending && <p role="status" aria-live="polite" className="muted">Recalculating every fingerprint in order. Large histories can take a little while.</p>}
      {verify.isError && <ErrorPanel error={verify.error} title="The check could not run" />}

      {r && (r.ok ? (
        // A broken chain is a RESULT shown prominently, never an error toast.
        <div className="result result-good" role="status">
          <div className="result-icon" aria-hidden="true">✓</div>
          <div>
            <h2>All good — nothing has been tampered with</h2>
            <p>
              <strong>{r.verifiedCount.toLocaleString()}</strong> record{r.verifiedCount === 1 ? '' : 's'} checked
              {r.fromSequence > 1 || r.toSequence < r.headSequence ? ` (records #${r.fromSequence.toLocaleString()} to #${r.toSequence.toLocaleString()})` : ''}.
              Every fingerprint matches.
            </p>
            <Notes r={r} />
          </div>
        </div>
      ) : (
        <div className="result result-bad" role="alert">
          <div className="result-icon" aria-hidden="true">!</div>
          <div>
            <h2>Problem found: record #{r.firstBrokenSequence?.toLocaleString()} {breakReason[r.reason ?? ''] ?? 'failed verification'}</h2>
            <p>
              The {r.verifiedCount.toLocaleString()} record{r.verifiedCount === 1 ? '' : 's'} before it are intact. Records from #
              {r.firstBrokenSequence?.toLocaleString()} onward can no longer be trusted until this is investigated.
            </p>
            {r.firstBrokenEventId
              ? <Link className="button primary" to={`/events/${r.firstBrokenEventId}`}>View record #{r.firstBrokenSequence?.toLocaleString()} →</Link>
              : <p className="muted">The record itself no longer exists, so there is nothing to open.</p>}
            <Notes r={r} />
          </div>
        </div>
      ))}
    </section>
  )
}

function Notes({ r }: { r: NonNullable<ReturnType<typeof useVerify>['data']> }) {
  return (
    <ul className="notes">
      <li>
        {r.checkpointUsed
          ? <>Started from a previously verified point (record #{r.checkpointUsed.sequence.toLocaleString()}, checked {relativeTime(r.checkpointUsed.createdAt)}).</>
          : <>Checked from the very first record.</>}
      </li>
      {r.purgedSkeletons > 0 && (
        <li>
          {r.purgedSkeletons.toLocaleString()} record{r.purgedSkeletons === 1 ? ' was' : 's were'} deleted by the retention policy (allowed).
          Their fingerprints were kept, so the chain still links correctly.
        </li>
      )}
    </ul>
  )
}

function useElapsedSeconds(running: boolean) {
  const [seconds, setSeconds] = useState(0)
  useEffect(() => {
    if (!running) return
    setSeconds(0)
    const timer = setInterval(() => setSeconds((s) => s + 1), 1000)
    return () => clearInterval(timer)
  }, [running])
  return seconds
}
