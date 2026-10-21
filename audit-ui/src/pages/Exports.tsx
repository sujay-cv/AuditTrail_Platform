import { useState } from 'react'
import { Link } from 'react-router-dom'
import { downloadExport } from '../api/client'
import { useCancelExport, useExports } from '../api/queries'
import { TERMINAL_EXPORT_STATUSES, type ExportJob } from '../api/types'
import { filtersToParams, type EventFilters } from '../lib/filters'
import { actorName, categoryLabel, formatDate, outcomeLabel, relativeTime } from '../lib/format'
import { CopyButton } from '../components/CopyButton'
import { ErrorPanel } from '../components/ErrorPanel'
import { Empty, ExportStatusBadge, Loading, PageHeader } from '../components/Status'

export function ExportsPage() {
  const exports = useExports()
  const cancel = useCancelExport()

  return (
    <section aria-label="Exports">
      <PageHeader
        title="Exports"
        subtitle="Download activity records as a file you can keep or hand to an external auditor."
        actions={<Link className="button primary" to="/events">+ New export</Link>}
      />
      <p className="hint">
        To create an export, search the <Link to="/events">Activity log</Link> and click <strong>Export results</strong>.
        This page updates by itself while an export is being prepared.
      </p>

      {cancel.isError && <ErrorPanel error={cancel.error} title="The export could not be cancelled" />}
      {exports.isPending && <Loading label="Loading exports…" />}
      {exports.isError && <ErrorPanel error={exports.error} onRetry={() => exports.refetch()} />}
      {exports.isRefetchError && exports.data && (
        <p className="field-error" role="alert">Live updates paused — showing the last known status.</p>
      )}
      {exports.data?.length === 0 && (
        <Empty title="No exports yet">Search the Activity log and click “Export results” to create one.</Empty>
      )}

      <div className="export-list">
        {exports.data?.map((job) => (
          <ExportCard key={job.id} job={job} onCancel={() => cancel.mutate(job.id)} cancelling={cancel.isPending && cancel.variables === job.id} />
        ))}
      </div>
    </section>
  )
}

/** "Financial · Denied · user:jdoe" + "1 Sep 2026 – 22 Oct 2026" */
function describe(f: ExportJob['filter']) {
  const parts = [
    f.category && categoryLabel[f.category],
    f.outcome && outcomeLabel[f.outcome],
    f.actorId && `by ${actorName(f.actorId)}`,
    f.actionPrefix && `“${f.actionPrefix}…” actions`,
    f.resourceType && f.resourceType,
    f.resourceId && f.resourceId,
  ].filter(Boolean)
  return {
    title: parts.length ? `${parts.join(' · ')} activity` : 'All activity',
    period: f.from && f.to ? `${formatDate(f.from)} – ${formatDate(f.to)}` : '',
  }
}

function ExportCard({ job, onCancel, cancelling }: { job: ExportJob; onCancel: () => void; cancelling: boolean }) {
  const active = !TERMINAL_EXPORT_STATUSES.includes(job.status)
  const { title, period } = describe(job.filter)
  const f = job.filter
  const searchLink = f.from && f.to ? `/events?${filtersToParams({ ...(f as unknown as EventFilters), from: f.from, to: f.to })}` : '/events'

  return (
    <article className={`card export-card ${active ? 'is-active' : ''}`} aria-label={`Export: ${title}`}>
      <div className="export-main">
        <div>
          <h2>{title}</h2>
          <p className="muted small">
            {period} · requested {relativeTime(job.createdAt)} by {job.requestedBy} · <Link to={searchLink}>view these records</Link>
          </p>
        </div>
        <ExportStatusBadge value={job.status} />
      </div>

      {active && (
        <div className="progress-wrap">
          <div className="progress" role="progressbar" aria-label="Export progress" aria-valuetext={`${job.rowCount} records written`}>
            <div className="progress-bar indeterminate" />
          </div>
          <span className="small" role="status">
            {job.status === 'Queued' ? 'Waiting for a free worker…' : `${job.rowCount.toLocaleString()} records written so far…`}
            {job.attempts > 1 && ` (resumed after an interruption, attempt ${job.attempts})`}
          </span>
        </div>
      )}

      {job.status === 'Completed' && (
        <p className="small">
          <strong>{job.rowCount.toLocaleString()}</strong> record{job.rowCount === 1 ? '' : 's'} · NDJSON file (one record per line)
          {job.attempts > 1 && ` · completed after resuming (attempt ${job.attempts})`}
        </p>
      )}
      {job.status === 'Failed' && <p className="field-error">This export failed{job.error ? `: ${job.error}` : ''}. Please create it again.</p>}
      {job.status === 'Expired' && <p className="muted small">The file was removed after its retention period. Create the export again if you need it.</p>}

      <div className="export-actions">
        {job.status === 'Completed' && <DownloadButton job={job} />}
        {active && (
          <button type="button" onClick={onCancel} disabled={cancelling}>{cancelling ? 'Cancelling…' : 'Cancel export'}</button>
        )}
        {job.checksum && (
          <details className="small">
            <summary>File fingerprint</summary>
            <p className="muted">SHA-256 of the file. Anyone can recompute it to prove the file wasn’t altered after download.</p>
            <code className="hash">{job.checksum}</code> <CopyButton value={job.checksum} label="file fingerprint" />
          </details>
        )}
      </div>
    </article>
  )
}

/**
 * Downloads with the sign-in headers, then recomputes the SHA-256 in the browser and compares it with the server's
 * X-Checksum-SHA256 header before saving, so the user knows the file arrived intact.
 */
function DownloadButton({ job }: { job: ExportJob }) {
  const [state, setState] = useState<{ status: 'idle' | 'busy' | 'ok' | 'mismatch'; error?: unknown }>({ status: 'idle' })

  async function download() {
    setState({ status: 'busy' })
    try {
      const { blob, checksum, fileName } = await downloadExport(job.id)
      const digest = await crypto.subtle.digest('SHA-256', await blob.arrayBuffer())
      const hex = Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, '0')).join('')

      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = fileName
      a.click()
      URL.revokeObjectURL(url)
      setState({ status: hex === checksum ? 'ok' : 'mismatch' })
    } catch (error) {
      setState({ status: 'idle', error })
    }
  }

  return (
    <>
      <button type="button" className="primary" onClick={download} disabled={state.status === 'busy'}>
        ⬇ {state.status === 'busy' ? 'Downloading…' : 'Download file'}
      </button>
      {state.status === 'ok' && <span className="small success-text" role="status">✓ Downloaded and verified — the file is complete and unaltered</span>}
      {state.status === 'mismatch' && <span className="small field-error" role="alert">✕ The downloaded file does not match its fingerprint. Please download it again.</span>}
      {state.error !== undefined && <ErrorPanel error={state.error} title="The download failed" />}
    </>
  )
}
