import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import { Link, useLocation, useSearchParams } from 'react-router-dom'
import { useVirtualizer } from '@tanstack/react-virtual'
import { MAX_LOADED_ROWS, useEventSearch, useProducers } from '../api/queries'
import { CATEGORIES, OUTCOMES, type Category, type EventListItem, type Outcome } from '../api/types'
import { defaultRange, filtersFromParams, filtersToParams, type EventFilters } from '../lib/filters'
import { actorName, actorTypeLabel, categoryLabel, displayName, formatDate, humanizeAction, outcomeLabel } from '../lib/format'
import { fromLocalInput, toLocalInput } from '../lib/time'
import { ErrorPanel } from '../components/ErrorPanel'
import { CategoryBadge, Empty, Loading, OutcomeBadge, PageHeader } from '../components/Status'
import { Time } from '../components/Time'
import { CreateExportButton } from '../components/CreateExportButton'

const ROW_HEIGHT = 58
const PRESETS = [
  { days: 1, label: 'Last 24 hours' },
  { days: 7, label: 'Last 7 days' },
  { days: 30, label: 'Last 30 days' },
  { days: 90, label: 'Last 90 days' },
]

function rangeEndingNow(days: number) {
  const to = new Date(Math.ceil(Date.now() / 60_000) * 60_000 + 60_000)
  return { from: new Date(to.getTime() - days * 86_400_000).toISOString(), to: to.toISOString() }
}

/** Which preset (if any) the current range matches: same length and ending about now. */
function activePreset(f: EventFilters): number | null {
  const days = (Date.parse(f.to) - Date.parse(f.from)) / 86_400_000
  const endsNow = Math.abs(Date.parse(f.to) - Date.now()) < 10 * 60_000
  return endsNow ? (PRESETS.find((p) => Math.abs(p.days - days) < 0.01)?.days ?? null) : null
}

export function EventExplorer() {
  const [params, setParams] = useSearchParams()
  const filters = useMemo(() => filtersFromParams(params), [params])

  // A time range is required: if the URL has none, put the default (last 30 days) in the URL.
  useEffect(() => {
    if (!filters) setParams(filtersToParams({ ...defaultRange() }), { replace: true })
  }, [filters, setParams])

  const apply = (f: EventFilters) => setParams(filtersToParams(f))

  return (
    <section aria-label="Activity log">
      <PageHeader
        title="Activity Log"
        subtitle="See who did what and whether it succeeded. Use the table filters to narrow the list."
      />
      {filters && (
        <Results filters={filters} onApply={apply} />
      )}
    </section>
  )
}

// ------------------------------------------------------------------------------------------------
// Filters
// ------------------------------------------------------------------------------------------------

function PeriodFilter({ filters, onApply }: { filters: EventFilters; onApply: (f: EventFilters) => void }) {
  const [draft, setDraft] = useState({ from: toLocalInput(filters.from), to: toLocalInput(filters.to) })
  const preset = activePreset(filters)
  const [showCustom, setShowCustom] = useState(preset === null)
  const [rangeError, setRangeError] = useState<string>()

  function submit(e: FormEvent) {
    e.preventDefault()
    const from = fromLocalInput(draft.from)
    const to = fromLocalInput(draft.to)
    if (!from || !to) return setRangeError('Please enter a start and end date.')
    const days = (Date.parse(to) - Date.parse(from)) / 86_400_000
    if (!(days > 0)) return setRangeError('The end date must be after the start date.')
    if (days > 90) return setRangeError('Please choose a period of 90 days or less.')
    setRangeError(undefined)
    onApply({ ...filters, from, to })
  }

  return (
    <form className="period-filter" onSubmit={submit} aria-label="Activity period">
      <label className="inline-label">
        Period
        <select value={showCustom || preset === null ? 'custom' : String(preset)}
          onChange={(e) => {
            if (e.target.value === 'custom') setShowCustom(true)
            else { setShowCustom(false); onApply({ ...filters, ...rangeEndingNow(Number(e.target.value)) }) }
          }}>
          {PRESETS.map((p) => <option key={p.days} value={p.days}>{p.label}</option>)}
          <option value="custom">Custom dates</option>
        </select>
      </label>

      {(showCustom || preset === null) && (
        <div className="custom-period">
          <label>
            From
            <input type="datetime-local" required value={draft.from}
              onChange={(e) => setDraft((d) => ({ ...d, from: e.target.value }))} />
          </label>
          <label>
            To
            <input type="datetime-local" required value={draft.to}
              onChange={(e) => setDraft((d) => ({ ...d, to: e.target.value }))} />
          </label>
          <button type="submit">Apply dates</button>
          <span className="hint">Up to 90 days, in your time zone.</span>
        </div>
      )}

      {rangeError && <p className="field-error" role="alert">{rangeError}</p>}
    </form>
  )
}

function TextFilter({ label, value, placeholder, onApply }: {
  label: string; value?: string; placeholder: string; onApply: (value: string | undefined) => void
}) {
  const [draft, setDraft] = useState(value ?? '')
  useEffect(() => setDraft(value ?? ''), [value])
  const commit = () => {
    const next = draft.trim() || undefined
    if (next !== value) onApply(next)
  }
  return (
    <input aria-label={label} aria-describedby="filter-help" value={draft} placeholder={placeholder}
      onChange={(e) => setDraft(e.target.value)} onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === 'Enter') { e.preventDefault(); commit() }
        if (e.key === 'Escape') setDraft(value ?? '')
      }} />
  )
}

/** Removable chips for every active filter, so it is always clear what is being shown. */
function ActiveFilters({ filters, producerName, onApply }: {
  filters: EventFilters; producerName: (id: string) => string; onApply: (f: EventFilters) => void
}) {
  const chips: { key: keyof EventFilters; text: string }[] = []
  if (filters.actorId) chips.push({ key: 'actorId', text: `Who: ${actorName(filters.actorId)}` })
  if (filters.actionPrefix) chips.push({ key: 'actionPrefix', text: `What: ${filters.actionPrefix}…` })
  if (filters.category) chips.push({ key: 'category', text: `Category: ${categoryLabel[filters.category]}` })
  if (filters.outcome) chips.push({ key: 'outcome', text: `Result: ${outcomeLabel[filters.outcome]}` })
  if (filters.resourceType) chips.push({ key: 'resourceType', text: `Item type: ${filters.resourceType}` })
  if (filters.resourceId) chips.push({ key: 'resourceId', text: `Item ID: ${filters.resourceId}` })
  if (filters.producerId) chips.push({ key: 'producerId', text: `Source: ${producerName(filters.producerId)}` })

  return (
    <div className="chips" aria-label="Active filters">
      <span className="muted small">
        Showing {formatDate(filters.from)} – {formatDate(filters.to)}
      </span>
      {chips.map((c) => (
        <span key={c.key} className="chip">
          {c.text}
          <button type="button" aria-label={`Remove filter ${c.text}`} onClick={() => onApply({ ...filters, [c.key]: undefined })}>×</button>
        </span>
      ))}
      {chips.length > 0 && (
        <button type="button" className="link-button" onClick={() => onApply({ from: filters.from, to: filters.to })}>Clear all</button>
      )}
    </div>
  )
}

// ------------------------------------------------------------------------------------------------
// Results: cursor pagination + virtualized rows
// ------------------------------------------------------------------------------------------------

function Results({ filters, onApply }: { filters: EventFilters; onApply: (f: EventFilters) => void }) {
  // The query key contains the filters: a new filter = a new query starting WITHOUT a cursor,
  // and the in-flight request for the old filter is aborted, so a stale page can never render.
  const search = useEventSearch(filters)
  const producers = useProducers()
  const location = useLocation()
  const rows = useMemo(() => (search.data?.pages.flatMap((p) => p.items) ?? []).slice(0, MAX_LOADED_ROWS), [search.data])
  const capped = rows.length >= MAX_LOADED_ROWS
  const canLoadMore = search.hasNextPage && !capped

  const scrollRef = useRef<HTMLDivElement>(null)
  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 10,
    initialRect: { width: 1000, height: 600 },
  })
  const items = virtualizer.getVirtualItems()

  useEffect(() => {
    if (scrollRef.current) scrollRef.current.scrollTop = 0
  }, [filters])

  const padTop = items[0]?.start ?? 0
  const padBottom = virtualizer.getTotalSize() - (items.at(-1)?.end ?? 0)
  const back = location.pathname + location.search
  const filterBy = (patch: Partial<EventFilters>) => onApply({ ...filters, ...patch })

  return (
    <div className="card results-card">
      <div className="results-bar">
        <PeriodFilter key={`${filters.from}/${filters.to}`} filters={filters} onApply={onApply} />
        <CreateExportButton filters={filters} />
      </div>
      <div className="table-filter-summary">
        <ActiveFilters filters={filters} producerName={(id) => displayName(producers.data?.find((p) => p.id === id)?.name ?? 'Selected system')} onApply={onApply} />
        <p id="filter-help" className="hint">Choose a dropdown to filter. For text, press Enter or leave the field. Click a person's name to filter by them; the Who field accepts their original ID.</p>
        {producers.isError && <ErrorPanel error={producers.error} title="Could not load source systems" onRetry={() => producers.refetch()} />}
      </div>
      <div className="results-count">
        <span role="status" aria-live="polite">
          {search.isPending ? 'Loading activity…' : search.isError && !search.data ? 'Activity could not be loaded' : <>
            <strong>{rows.length.toLocaleString()}</strong> {rows.length === 1 ? 'record' : 'records'} shown
            {capped ? ' (limit reached — narrow your filters or export)' : ''}
          </>}
        </span>
      </div>

      <div ref={scrollRef} className="table-scroll" tabIndex={0} aria-label="Activity records, scrollable">
        <table className="events" aria-label="Activity records">
          <thead>
            <tr>
              <th scope="col">When</th>
              <th scope="col">Who
                <TextFilter label="Who (exact ID)" value={filters.actorId} placeholder="e.g. user:jdoe" onApply={(actorId) => filterBy({ actorId })} />
              </th>
              <th scope="col">What Happened
                <TextFilter label="Action starts with" value={filters.actionPrefix} placeholder="e.g. payment" onApply={(actionPrefix) => filterBy({ actionPrefix })} />
              </th>
              <th scope="col">Item
                <TextFilter label="Item type" value={filters.resourceType} placeholder="Type, e.g. Invoice" onApply={(resourceType) => filterBy({ resourceType })} />
                <TextFilter label="Item ID" value={filters.resourceId} placeholder="ID, e.g. INV-20931" onApply={(resourceId) => filterBy({ resourceId })} />
              </th>
              <th scope="col">Result
                <select aria-label="Result" value={filters.outcome ?? ''} onChange={(e) => filterBy({ outcome: (e.target.value || undefined) as Outcome | undefined })}>
                  <option value="">Any result</option>
                  {OUTCOMES.map((o) => <option key={o} value={o}>{outcomeLabel[o]}</option>)}
                </select>
              </th>
              <th scope="col">Category
                <select aria-label="Category" value={filters.category ?? ''} onChange={(e) => filterBy({ category: (e.target.value || undefined) as Category | undefined })}>
                  <option value="">All categories</option>
                  {CATEGORIES.map((c) => <option key={c} value={c}>{categoryLabel[c]}</option>)}
                </select>
              </th>
              <th scope="col">Source
                <select aria-label="Source system" disabled={producers.isPending || producers.isError} value={filters.producerId ?? ''} onChange={(e) => filterBy({ producerId: e.target.value || undefined })}>
                  <option value="">{producers.isPending ? 'Loading systems…' : producers.isError ? 'Systems unavailable' : 'All systems'}</option>
                  {producers.data?.map((p) => <option key={p.id} value={p.id}>{displayName(p.name)}</option>)}
                </select>
              </th>
            </tr>
          </thead>
          <tbody>
            {search.isPending && <tr><td colSpan={7}><Loading label="Loading activity…" /></td></tr>}
            {search.isError && !search.data && <tr><td colSpan={7}><ErrorPanel error={search.error} onRetry={() => search.refetch()} /></td></tr>}
            {!search.isPending && !search.isError && rows.length === 0 && <tr><td colSpan={7}>
              <Empty title="No activity found">Try a longer time period or remove some filters.</Empty>
            </td></tr>}
            {padTop > 0 && <tr aria-hidden="true" className="spacer"><td colSpan={7} style={{ height: padTop }} /></tr>}
            {items.map((v) => <Row key={rows[v.index].id} e={rows[v.index]} back={back} filterBy={filterBy} />)}
            {padBottom > 0 && <tr aria-hidden="true" className="spacer"><td colSpan={7} style={{ height: padBottom }} /></tr>}
          </tbody>
        </table>
      </div>

      <div className="results-footer">
        {search.isFetchingNextPage && <Loading label="Loading more…" />}
        {search.isFetchNextPageError && <ErrorPanel error={search.error} title="Couldn’t load more results (the ones above are still correct)" />}
        {canLoadMore && !search.isFetchingNextPage && (
          <button type="button" onClick={() => search.fetchNextPage()}>Load more</button>
        )}
        {!canLoadMore && !capped && rows.length > 0 && <span className="muted small">End of results</span>}
      </div>
    </div>
  )
}

function Row({ e, back, filterBy }: { e: EventListItem; back: string; filterBy: (p: Partial<EventFilters>) => void }) {
  return (
    <tr style={{ height: ROW_HEIGHT }}>
      <td><Time iso={e.occurredAt} relative /></td>
      <td>
        <button type="button" className="link-button strong" title={`Show only activity by ${e.actorId}`} onClick={() => filterBy({ actorId: e.actorId })}>
          {actorName(e.actorId)}
        </button>
        <div className="muted small">{actorTypeLabel[e.actorType] ?? e.actorType}</div>
      </td>
      <td>
        <Link to={`/events/${e.id}`} state={{ back }} className="strong">{humanizeAction(e.action)}</Link>
        <div className="muted small">View details →</div>
      </td>
      <td>
        <span className="muted small">{e.resourceType}</span>
        <div><strong>{e.resourceId}</strong></div>
      </td>
      <td><OutcomeBadge value={e.outcome} /></td>
      <td><CategoryBadge value={e.category} /></td>
      <td className="muted">{displayName(e.producerName)}</td>
    </tr>
  )
}
