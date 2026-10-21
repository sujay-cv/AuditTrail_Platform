import { describe, expect, it } from 'vitest'
import { defaultRange, filtersFromParams, filtersToParams, type EventFilters } from './filters'

describe('filters <-> URL', () => {
  it('round-trips every filter through the URL', () => {
    const filters: EventFilters = {
      from: '2026-09-01T00:00:00.000Z',
      to: '2026-10-01T00:00:00.000Z',
      actorId: 'user:jdoe',
      actionPrefix: 'payment.',
      resourceType: 'Invoice',
      resourceId: 'INV-1',
      outcome: 'Denied',
      category: 'Financial',
      producerId: '0b3c8e7e-1111-2222-3333-444455556666',
      sort: 'oldest',
    }
    const url = filtersToParams(filters).toString()
    expect(filtersFromParams(new URLSearchParams(url))).toEqual(filters)
  })

  it('omits empty filters so equal searches give equal URLs', () => {
    const params = filtersToParams({ from: '2026-09-01T00:00:00.000Z', to: '2026-10-01T00:00:00.000Z', actorId: '' })
    expect([...params.keys()]).toEqual(['from', 'to'])
  })

  it('returns null without a time range and drops invalid enum values', () => {
    expect(filtersFromParams(new URLSearchParams('actorId=x'))).toBeNull()
    const parsed = filtersFromParams(new URLSearchParams('from=2026-09-01T00:00:00Z&to=2026-09-02T00:00:00Z&outcome=Maybe&category=Gossip'))
    expect(parsed).toEqual({ from: '2026-09-01T00:00:00.000Z', to: '2026-09-02T00:00:00.000Z' })
  })

  it('default range is 30 days and ends after now', () => {
    const now = new Date('2026-10-21T10:15:30Z')
    const { from, to } = defaultRange(now)
    expect(Date.parse(to)).toBeGreaterThan(now.getTime())
    expect((Date.parse(to) - Date.parse(from)) / 86_400_000).toBe(30)
  })
})
