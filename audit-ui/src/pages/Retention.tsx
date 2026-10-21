import { useState, type FormEvent } from 'react'
import { useCreateHold, useHolds, usePolicies, useReleaseHold, useRunRetention, useUpdatePolicy } from '../api/queries'
import { CATEGORIES, type Category, type HoldScopeType, type LegalHold, type RetentionPolicy } from '../api/types'
import { actorName, categoryHelp, categoryLabel, humanDays, relativeTime } from '../lib/format'
import { useIsAdmin } from '../session/useSession'
import { Dialog } from '../components/Dialog'
import { ErrorPanel, FieldError } from '../components/ErrorPanel'
import { Empty, Loading, PageHeader } from '../components/Status'

const ADMIN_ONLY = 'Only Compliance admins can change this'

/**
 * Auditors see everything read-only, with buttons disabled and an explanation.
 * The API still enforces it: if it answers 403 anyway, the error box says "Not allowed" with a reference code.
 */
export function RetentionPage() {
  const isAdmin = useIsAdmin()
  return (
    <section aria-label="Retention and legal holds">
      <PageHeader
        title="Retention & legal holds"
        subtitle="Decide how long activity records are kept, and protect records that must not be deleted."
      />
      {!isAdmin && (
        <div className="alert alert-info" role="note">
          <div className="alert-icon" aria-hidden="true">i</div>
          <div className="alert-body">
            <strong>View only</strong>
            <p>You are signed in as an Auditor. Switch to “Compliance admin” (top right) to make changes.</p>
          </div>
        </div>
      )}
      <Policies isAdmin={isAdmin} />
      <Holds isAdmin={isAdmin} />
      <CleanUp isAdmin={isAdmin} />
    </section>
  )
}

// ------------------------------------------------------------------------------------------------
// Retention policies
// ------------------------------------------------------------------------------------------------

function Policies({ isAdmin }: { isAdmin: boolean }) {
  const policies = usePolicies()
  const [editing, setEditing] = useState<RetentionPolicy | null>(null)

  return (
    <div className="card">
      <h2>How long records are kept</h2>
      <p className="hint">Records older than this are deleted automatically, unless a legal hold protects them.</p>
      {policies.isPending && <Loading />}
      {policies.isError && <ErrorPanel error={policies.error} onRetry={() => policies.refetch()} />}
      {policies.data && (
        <div className="policy-grid">
          {policies.data.map((p) => (
            <div key={p.category} className="policy">
              <div>
                <strong>{categoryLabel[p.category]}</strong>
                <p className="muted small">{categoryHelp[p.category]}</p>
              </div>
              <div className="policy-value">
                <span className="big-number">{humanDays(p.retentionDays)}</span>
                <span className="muted small">changed {relativeTime(p.updatedAt)}</span>
              </div>
              <button type="button" disabled={!isAdmin} title={isAdmin ? undefined : ADMIN_ONLY} onClick={() => setEditing(p)}>
                Change
              </button>
            </div>
          ))}
        </div>
      )}
      <Dialog open={editing !== null} title={`Keep ${editing ? categoryLabel[editing.category].toLowerCase() : ''} records for…`} onClose={() => setEditing(null)}>
        {editing && <EditPolicyForm policy={editing} onDone={() => setEditing(null)} />}
      </Dialog>
    </div>
  )
}

const QUICK_DAYS = [30, 90, 365, 365 * 3, 365 * 7]

function EditPolicyForm({ policy, onDone }: { policy: RetentionPolicy; onDone: () => void }) {
  const [days, setDays] = useState(String(policy.retentionDays))
  const [confirmed, setConfirmed] = useState(false)
  const update = useUpdatePolicy()
  const value = Number(days)
  const valid = Number.isInteger(value) && value >= 1 && value <= 36500
  const shorter = valid && value < policy.retentionDays

  function submit(e: FormEvent) {
    e.preventDefault()
    update.mutate({ category: policy.category as Category, retentionDays: value }, { onSuccess: onDone })
  }

  return (
    <form onSubmit={submit}>
      <div className="segmented" role="group" aria-label="Common periods">
        {QUICK_DAYS.map((d) => (
          <button key={d} type="button" aria-pressed={value === d} onClick={() => { setDays(String(d)); setConfirmed(false) }}>
            {humanDays(d)}
          </button>
        ))}
      </div>
      <label>
        Number of days
        <input type="number" min={1} max={36500} required value={days} onChange={(e) => { setDays(e.target.value); setConfirmed(false) }} />
        {valid && <span className="hint">= {humanDays(value)}</span>}
        <FieldError error={update.error} field="retentionDays" />
      </label>
      {shorter && (
        <div className="alert alert-warning">
          <div className="alert-icon" aria-hidden="true">!</div>
          <div className="alert-body">
            <strong>Records will be deleted</strong>
            <p>{categoryLabel[policy.category]} records older than {humanDays(value)} will be permanently deleted at the next clean-up, unless a legal hold protects them.</p>
          </div>
        </div>
      )}
      <label className="checkbox">
        <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />
        I understand and want to change this to {valid ? humanDays(value) : '…'}
      </label>
      {update.isError && <ErrorPanel error={update.error} />}
      <div className="dialog-actions">
        <button type="button" onClick={onDone}>Cancel</button>
        <button type="submit" className="primary" disabled={!confirmed || !valid || update.isPending}>
          {update.isPending ? 'Saving…' : 'Save change'}
        </button>
      </div>
    </form>
  )
}

// ------------------------------------------------------------------------------------------------
// Legal holds
// ------------------------------------------------------------------------------------------------

function describeHold(h: LegalHold) {
  if (h.scopeType === 'Actor') return `All activity by ${actorName(h.scopeValue)}`
  if (h.scopeType === 'Category') return `All ${categoryLabel[h.scopeValue as Category]?.toLowerCase() ?? h.scopeValue} activity`
  return `All activity on ${h.scopeValue.replace('/', ' ')}`
}

function Holds({ isAdmin }: { isAdmin: boolean }) {
  const holds = useHolds()
  const [releasing, setReleasing] = useState<LegalHold | null>(null)
  const [creating, setCreating] = useState(false)
  const active = holds.data?.filter((h) => h.active) ?? []
  const released = holds.data?.filter((h) => !h.active) ?? []

  return (
    <div className="card">
      <div className="card-header">
        <div>
          <h2>Legal holds</h2>
          <p className="hint">A legal hold stops records from being deleted, for example during an investigation or a lawsuit — even if they are older than the retention period.</p>
        </div>
        <button type="button" className="primary" disabled={!isAdmin} title={isAdmin ? undefined : ADMIN_ONLY} onClick={() => setCreating(true)}>
          + Place a hold
        </button>
      </div>

      {holds.isPending && <Loading />}
      {holds.isError && <ErrorPanel error={holds.error} onRetry={() => holds.refetch()} />}
      {holds.data && active.length === 0 && <Empty title="No active legal holds">Records are deleted normally when their retention period ends.</Empty>}

      <ul className="hold-list">
        {active.map((h) => (
          <li key={h.id} className="hold">
            <span className="hold-icon" aria-hidden="true">🔒</span>
            <div className="grow">
              <strong>{describeHold(h)}</strong>
              <p className="small">“{h.reason}”</p>
              <p className="muted small">Placed by {h.createdBy} · {relativeTime(h.createdAt)}</p>
            </div>
            <button type="button" disabled={!isAdmin} title={isAdmin ? undefined : ADMIN_ONLY} onClick={() => setReleasing(h)}>Release</button>
          </li>
        ))}
      </ul>

      {released.length > 0 && (
        <details>
          <summary className="small">Released holds ({released.length})</summary>
          <ul className="hold-list">
            {released.map((h) => (
              <li key={h.id} className="hold released">
                <span className="hold-icon" aria-hidden="true">🔓</span>
                <div className="grow">
                  <strong>{describeHold(h)}</strong>
                  <p className="small">“{h.reason}”</p>
                  <p className="muted small">Placed by {h.createdBy} {relativeTime(h.createdAt)} · released by {h.releasedBy} {h.releasedAt ? relativeTime(h.releasedAt) : ''}</p>
                </div>
              </li>
            ))}
          </ul>
        </details>
      )}

      <Dialog open={creating} title="Place a legal hold" onClose={() => setCreating(false)}>
        <CreateHoldForm onDone={() => setCreating(false)} />
      </Dialog>
      <Dialog open={releasing !== null} title="Release legal hold" onClose={() => setReleasing(null)}>
        {releasing && <ReleaseHoldForm hold={releasing} onDone={() => setReleasing(null)} />}
      </Dialog>
    </div>
  )
}

const SCOPES: { value: HoldScopeType; label: string; help: string; placeholder: string }[] = [
  { value: 'Actor', label: 'A person or system', help: 'Everything this person or system did', placeholder: 'e.g. user:jdoe' },
  { value: 'Resource', label: 'A specific item', help: 'Everything that happened to one item (Type/ID)', placeholder: 'e.g. Invoice/INV-20931' },
  { value: 'Category', label: 'A whole category', help: 'Every record in one category', placeholder: '' },
]

function CreateHoldForm({ onDone }: { onDone: () => void }) {
  const [scopeType, setScopeType] = useState<HoldScopeType>('Actor')
  const [scopeValue, setScopeValue] = useState('')
  const [reason, setReason] = useState('')
  const create = useCreateHold()
  const scope = SCOPES.find((s) => s.value === scopeType)!

  function submit(e: FormEvent) {
    e.preventDefault()
    create.mutate({ scopeType, scopeValue: scopeValue.trim(), reason: reason.trim() }, { onSuccess: onDone })
  }

  return (
    <form onSubmit={submit}>
      <fieldset className="radio-cards">
        <legend>What should be protected?</legend>
        {SCOPES.map((s) => (
          <label key={s.value} className={`radio-card ${scopeType === s.value ? 'selected' : ''}`}>
            <input type="radio" name="scope" value={s.value} checked={scopeType === s.value}
              onChange={() => { setScopeType(s.value); setScopeValue(s.value === 'Category' ? 'Financial' : '') }} />
            <span><strong>{s.label}</strong><span className="muted small">{s.help}</span></span>
          </label>
        ))}
      </fieldset>

      {scopeType === 'Category' ? (
        <label>
          Category
          <select value={scopeValue} onChange={(e) => setScopeValue(e.target.value)}>
            {CATEGORIES.map((c) => <option key={c} value={c}>{categoryLabel[c]}</option>)}
          </select>
        </label>
      ) : (
        <label>
          {scopeType === 'Actor' ? 'Person or system ID' : 'Item (Type/ID)'}
          <input required value={scopeValue} onChange={(e) => setScopeValue(e.target.value)} placeholder={scope.placeholder} />
          <FieldError error={create.error} field="scopeValue" />
        </label>
      )}
      <label>
        Reason (required — recorded in the audit trail)
        <textarea required rows={3} value={reason} onChange={(e) => setReason(e.target.value)} placeholder="e.g. Litigation hold for case 2026-17" />
        <FieldError error={create.error} field="reason" />
      </label>
      {create.isError && <ErrorPanel error={create.error} title="The hold could not be placed" />}
      <div className="dialog-actions">
        <button type="button" onClick={onDone}>Cancel</button>
        <button type="submit" className="primary" disabled={create.isPending || !scopeValue.trim() || !reason.trim()}>
          {create.isPending ? 'Placing hold…' : 'Place hold'}
        </button>
      </div>
    </form>
  )
}

function ReleaseHoldForm({ hold, onDone }: { hold: LegalHold; onDone: () => void }) {
  const [reason, setReason] = useState('')
  const release = useReleaseHold()

  function submit(e: FormEvent) {
    e.preventDefault()
    release.mutate({ id: hold.id, reason: reason.trim() }, { onSuccess: onDone })
  }

  return (
    <form onSubmit={submit}>
      <p><strong>{describeHold(hold)}</strong> will no longer be protected. Old records may be deleted at the next clean-up.</p>
      <label>
        Reason for releasing (required)
        <textarea required rows={3} value={reason} onChange={(e) => setReason(e.target.value)} placeholder="e.g. Case closed" />
        <FieldError error={release.error} field="reason" />
      </label>
      {release.isError && <ErrorPanel error={release.error} />}
      <div className="dialog-actions">
        <button type="button" onClick={onDone}>Cancel</button>
        <button type="submit" className="danger" disabled={!reason.trim() || release.isPending}>{release.isPending ? 'Releasing…' : 'Release hold'}</button>
      </div>
    </form>
  )
}

// ------------------------------------------------------------------------------------------------
// Run clean-up now
// ------------------------------------------------------------------------------------------------

function CleanUp({ isAdmin }: { isAdmin: boolean }) {
  const [open, setOpen] = useState(false)
  const run = useRunRetention()
  return (
    <div className="card">
      <div className="card-header">
        <div>
          <h2>Clean-up</h2>
          <p className="hint">Expired records are deleted automatically every few minutes. You can also run the clean-up now. A tamper check runs first — if it finds a problem, nothing is deleted.</p>
        </div>
        <button type="button" disabled={!isAdmin || run.isPending} title={isAdmin ? undefined : ADMIN_ONLY} onClick={() => setOpen(true)}>
          {run.isPending ? 'Cleaning up…' : 'Run clean-up now'}
        </button>
      </div>
      {run.isSuccess && (
        <div className="alert alert-success" role="status">
          <div className="alert-icon" aria-hidden="true">✓</div>
          <div className="alert-body">
            <strong>Clean-up finished</strong>
            <p>{run.data.deleted === 0 ? 'No records were due for deletion.' : `${run.data.deleted.toLocaleString()} expired record(s) were deleted. Records under a legal hold were kept.`}</p>
          </div>
        </div>
      )}
      {run.isError && <ErrorPanel error={run.error} title="The clean-up did not run" />}
      <Dialog open={open} title="Delete expired records now?" onClose={() => setOpen(false)}>
        <p>Records older than their retention period will be <strong>permanently deleted</strong>. Records under an active legal hold are always kept.</p>
        <div className="dialog-actions">
          <button type="button" onClick={() => setOpen(false)}>Cancel</button>
          <button type="button" className="danger" onClick={() => { setOpen(false); run.mutate() }}>Delete expired records</button>
        </div>
      </Dialog>
    </div>
  )
}
