import { CATEGORIES, OUTCOMES, type Category, type Outcome } from '../api/types'

/**
 * Explorer filters. They live in the URL (?from=...&to=...&category=...), so a search can be shared and reloaded.
 * from/to are UTC ISO strings: the URL and the API only ever see UTC.
 */
export interface EventFilters {
  from: string
  to: string
  actorId?: string
  actionPrefix?: string
  resourceType?: string
  resourceId?: string
  outcome?: Outcome
  category?: Category
  producerId?: string
  /** Display order. Newest first unless 'oldest' (only sent to search, never part of an export). */
  sort?: 'oldest'
}

const TEXT_KEYS = ['actorId', 'actionPrefix', 'resourceType', 'resourceId', 'producerId'] as const

/** Default range: the last 30 days, rounded up to the next minute (so the URL is stable and includes "now"). */
export function defaultRange(now = new Date()): { from: string; to: string } {
  const to = new Date(Math.ceil(now.getTime() / 60_000) * 60_000 + 60_000)
  const from = new Date(to.getTime() - 30 * 86_400_000)
  return { from: from.toISOString(), to: to.toISOString() }
}

function validIso(value: string | null): string | undefined {
  return value && !Number.isNaN(Date.parse(value)) ? new Date(value).toISOString() : undefined
}

/** URL -> filters. Unknown or invalid values are dropped. Returns null when the time range is missing. */
export function filtersFromParams(params: URLSearchParams): EventFilters | null {
  const from = validIso(params.get('from'))
  const to = validIso(params.get('to'))
  if (!from || !to) return null

  const filters: EventFilters = { from, to }
  for (const key of TEXT_KEYS) {
    const v = params.get(key)?.trim()
    if (v) filters[key] = v
  }
  const outcome = params.get('outcome')
  if (outcome && (OUTCOMES as string[]).includes(outcome)) filters.outcome = outcome as Outcome
  const category = params.get('category')
  if (category && (CATEGORIES as string[]).includes(category)) filters.category = category as Category
  if (params.get('sort') === 'oldest') filters.sort = 'oldest'
  return filters
}

/** Filters -> URL. Empty values are omitted; key order is fixed so equal filters give equal URLs. */
export function filtersToParams(filters: EventFilters): URLSearchParams {
  const params = new URLSearchParams()
  params.set('from', filters.from)
  params.set('to', filters.to)
  for (const key of [...TEXT_KEYS, 'outcome', 'category', 'sort'] as const) {
    const v = filters[key]
    if (v) params.set(key, v)
  }
  return params
}
