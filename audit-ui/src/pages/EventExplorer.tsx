import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import { Link, useLocation, useSearchParams } from 'react-router-dom'
import { useVirtualizer } from '@tanstack/react-virtual'
import { MAX_LOADED_ROWS, useEventSearch, useProducers } from '../api/queries'
import { CATEGORIES, OUTCOMES, type Category, type EventListItem, type Outcome } from '../api/types'
import { defaultRange, filtersFromParams, filtersToParams, type EventFilters } from '../lib/filters'
import { actorName, actorTypeLabel, categoryLabel, formatDate, humanizeAction, outcomeLabel } from '../lib/format'
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
        title="Activity log"
        subtitle="Every recorded action in your organization: who did what, to which item, when, and whether it worked."
      />
      {filters && (
        <>
          <FilterBar key={params.toString()} filters={filters} onApply={apply} />
          <Results filters={filters} onApply={apply} />
        </>
      )}
    </section>
  )
}

// ------------------------------------------------------------------------------------------------
// Filters
// ------------------------------------------------------------------------------------------------

function FilterBar({ filters, onApply }: { filters: EventFilters; onApply: (f: EventFilters) => void }) {
  const [draft, setDraft] = useState<EventFilters>(filters)
  const preset = activePreset(filters)
  const [showCustom, setShowCustom] = useState(preset === null)
  const [showMore, setShowMore] = useState(Boolean(filters.resourceType || filters.resourceId || filters.producerId))
  const [rangeError, setRangeError] = useState<string>()
  const producers = useProducers()

  const setText = (key: 'actorId' | 'actionPrefix' | 'resourceType' | 'resourceId', value: string) =>
    setDraft((d) => ({ ...d, [key]: value || undefined }))

  /** Dropdowns apply immediately; text boxes apply on Search / Enter. */
  const applyNow = (patch: Partial<EventFilters>) => onApply({ ...draft, ...patch })

  function submit(e: FormEvent) {
    e.preventDefault()
    const days = (Date.parse(draft.to) - Date.parse(draft.from)) / 86_400_000
    if (!(days > 0)) return setRangeError('The end date must be after the start date.')
    if (days > 90) return setRangeError('Please choose a period of 90 days or less.')
    setRangeError(undefined)
    onApply(draft)
  }

  return (
    <form className="card filter-card" onSubmit={submit} aria-label="Search filters">
      <div className="segmented" role="group" aria-label="Time period">
        {PRESETS.map((p) => (
          <button key={p.days} type="button" aria-pressed={preset === p.days && !showCustom}
            onClick={() => { setShowCustom(false); applyNow(rangeEndingNow(p.days)) }}>
            {p.label}
          </button>
        ))}
        <button type="button" aria-pressed={showCustom || preset === null} onClick={() => setShowCustom(true)}>
          Custom dates
        </button>
      </div>

      {(showCustom || preset === null) && (
        <div className="filter-row">
          <label>
            From
            <input type="datetime-local" required value={toLocalInput(draft.from)}
              onChange={(e) => { const v = fromLocalInput(e.target.value); if (v) setDraft((d) => ({ ...d, from: v })) }} />
          </label>
          <label>
            To
            <input type="datetime-local" required value={toLocalInput(draft.to)}
              onChange={(e) => { const v = fromLocalInput(e.target.value); if (v) setDraft((d) => ({ ...d, to: v })) }} />
          </label>
          <p className="hint">Up to 90 days at a time. Times are in your time zone.</p>
        </div>
      )}

      <div className="filter-row">
        <label className="grow">
          Who
          <input value={draft.actorId ?? ''} onChange={(e) => setText('actorId', e.target.value)} placeholder="e.g. user:jdoe" />
        </label>
        <label className="grow">
          What happened (starts with)
          <input value={draft.actionPrefix ?? ''} onChange={(e) => setText('actionPrefix', e.target.value)} placeholder="e.g. payment" />
        </label>
        <label>
          Category
          <select value={draft.category ?? ''} onChange={(e) => applyNow({ category: (e.target.value || undefined) as Category | undefined })}>
            <option value="">All categories</option>
            {CATEGORIES.map((c) => <option key={c} value={c}>{categoryLabel[c]}</option>)}
          </select>
        </label>
        <label>
          Result
          <select value={draft.outcome ?? ''} onChange={(e) => applyNow({ outcome: (e.target.value || undefined) as Outcome | undefined })}>
            <option value="">Any result</option>
            {OUTCOMES.map((o) => <option key={o} value={o}>{outcomeLabel[o]}</option>)}
          </select>
        </label>
        <button type="submit" className="primary">Search</button>
      </div>

      <button type="button" className="link-button" aria-expanded={showMore} onClick={() => setShowMore((v) => !v)}>
        {showMore ? '− Fewer filters' : '+ More filters'}
      </button>
      {showMore && (
        <div className="filter-row">
          <label className="grow">
            Item type
            <input value={draft.resourceType ?? ''} onChange={(e) => setText('resourceType', e.target.value)} placeholder="e.g. Invoice" />
          </label>
          <label className="grow">
            Item ID
            <input value={draft.resourceId ?? ''} onChange={(e) => setText('resourceId', e.target.value)} placeholder="e.g. INV-20931" />
          </label>
          <label className="grow">
            Source system
            <select value={draft.producerId ?? ''} onChange={(e) => applyNow({ producerId: e.target.value || undefined })}>
              <option value="">All systems</option>
              {producers.data?.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          </label>
        </div>
      )}
      {rangeError && <p className="field-error" role="alert">{rangeError}</p>}

      <ActiveFilters filters={filters} producerName={(id) => producers.data?.find((p) => p.id === id)?.name ?? 'Selected system'} onApply={onApply} />
    </form>
  )
}

/** Removable chips for every active filter, so it is always clear what is being shown. */
function ActiveFilters({ filters, producerName, onApply }: {
  filters: EventFilters; producerName: (id: string) => string; onApply: (f: EventFilters) => void
}) {
  const chips: { key: keyof EventFilters; text: string }[] = []
  if (filters.actorId) chips.push({ key: 'actorId', text: `Who: ${filters.actorId}` })
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
      {chips.length > 1 && (
        <button type="button" className="link-button" onClick={() => onApply({ from: filters.from, to: filters.to, sort: filters.sort })}>Clear all</button>
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

  // Infinite scroll: fetch the next page when the user scrolls near the end of what is loaded.
  const lastIndex = items.at(-1)?.index ?? -1
  useEffect(() => {
    if (canLoadMore && !search.isFetchingNextPage && !search.isFetchNextPageError && lastIndex >= rows.length - 15) search.fetchNextPage()
  }, [lastIndex, rows.length, canLoadMore, search])

  if (search.isPending) return <Loading label="Searching activity…" />
  // Page-level error only when nothing could be loaded; a failed "load more" keeps the rows already shown.
  if (search.isError && !search.data) return <ErrorPanel error={search.error} onRetry={() => search.refetch()} />
  if (rows.length === 0)
    return (
      <Empty title="No activity found">
        Nothing matches these filters in this period. Try a longer time period or remove some filters.
      </Empty>
    )

  const padTop = items[0]?.start ?? 0
  const padBottom = virtualizer.getTotalSize() - (items.at(-1)?.end ?? 0)
  const back = location.pathname + location.search
  const filterBy = (patch: Partial<EventFilters>) => onApply({ ...filters, ...patch })

  return (
    <div className="card results-card">
      <div className="results-bar">
        <span role="status" aria-live="polite">
          <strong>{rows.length.toLocaleString()}</strong> {rows.length === 1 ? 'activity' : 'activities'}
          {canLoadMore ? ' shown · scroll down for more' : capped ? ` shown (limit reached — narrow your filters or export)` : ''}
        </span>
        <div className="results-tools">
          <label className="inline-label">
            Sort
            <select value={filters.sort ?? 'newest'} onChange={(e) => filterBy({ sort: e.target.value === 'oldest' ? 'oldest' : undefined })}>
              <option value="newest">Newest first</option>
              <option value="oldest">Oldest first</option>
            </select>
          </label>
          <CreateExportButton filters={filters} />
        </div>
      </div>

      <div ref={scrollRef} className="table-scroll" tabIndex={0} aria-label="Search results, scrollable">
        <table className="events">
          <thead>
            <tr>
              <th scope="col">When</th>
              <th scope="col">Who</th>
              <th scope="col">What happened</th>
              <th scope="col">Item</th>
              <th scope="col">Result</th>
              <th scope="col">Category</th>
              <th scope="col">Source</th>
            </tr>
          </thead>
          <tbody>
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
        {!canLoadMore && !capped && <span className="muted small">End of results</span>}
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
      <td className="muted">{e.producerName}</td>
    </tr>
  )
}
