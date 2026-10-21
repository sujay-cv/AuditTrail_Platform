import { useRef } from 'react'
import { Link } from 'react-router-dom'
import { useCreateExport } from '../api/queries'
import type { EventFilters } from '../lib/filters'
import { ErrorPanel } from './ErrorPanel'

/**
 * "Export results" button.
 *
 * Idempotency: ONE Idempotency-Key per user intent ("export these filters"). The key is reused for double-clicks and
 * for a retry after a failure (the server then returns the SAME job instead of creating a second one). It is replaced
 * only after a success (a later click is a new request) or when the filters change.
 */
export function CreateExportButton({ filters }: { filters: EventFilters }) {
  const create = useCreateExport()
  const intent = useRef<{ filters: string; key: string } | null>(null)

  function onClick() {
    const signature = JSON.stringify({ ...filters, sort: undefined })   // sort order doesn't change what is exported
    if (intent.current?.filters !== signature) intent.current = { filters: signature, key: crypto.randomUUID() }
    const key = intent.current.key
    create.mutate(
      { filters, idempotencyKey: key },
      { onSuccess: () => { if (intent.current?.key === key) intent.current = null } },
    )
  }

  return (
    <div className="export-create">
      <button type="button" className="secondary" onClick={onClick} disabled={create.isPending} aria-busy={create.isPending}>
        <span aria-hidden="true">⬇</span> {create.isPending ? 'Starting export…' : 'Export results'}
      </button>
      {create.isSuccess && (
        <span role="status" className="small success-text">
          ✓ Export started. <Link to="/exports">Go to Exports</Link> to download it when it’s ready.
        </span>
      )}
      {create.isError && <ErrorPanel error={create.error} title="The export could not be started" />}
    </div>
  )
}
