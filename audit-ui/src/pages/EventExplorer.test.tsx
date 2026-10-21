import { afterEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { EventExplorer } from './EventExplorer'
import { hangUntilAborted, mockFetch, renderWithProviders, type Call } from '../test-utils'

afterEach(() => vi.unstubAllGlobals())

const item = (n: number, actor: string) => ({
  id: `id-${actor}-${n}`, eventId: `e-${n}`, sequence: n, occurredAt: '2026-09-15T10:00:00Z', receivedAt: '2026-09-15T10:00:01Z',
  actorType: 'User', actorId: actor, action: 'record.viewed', resourceType: 'Record', resourceId: `R-${n}`,
  outcome: 'Success', category: 'Access', producerId: 'p1', producerName: 'producer-1',
})

const URL0 = '/events?from=2026-09-01T00%3A00%3A00.000Z&to=2026-10-01T00%3A00%3A00.000Z'
const eventCalls = (calls: Call[]) => calls.filter((c) => c.url.pathname === '/api/events')

describe('Activity log (EventExplorer)', () => {
  it('shows records in plain language', async () => {
    mockFetch((c) => c.url.pathname === '/api/producers'
      ? { status: 200, body: [] }
      : { status: 200, body: { items: [item(1, 'user:alice')], nextCursor: null } })
    renderWithProviders(<EventExplorer />, { url: URL0, path: '/events' })

    expect(await screen.findByRole('link', { name: 'Record viewed' })).toBeInTheDocument()   // "record.viewed"
    expect(screen.getByRole('button', { name: 'alice' })).toBeInTheDocument()                // "user:alice"
    expect(screen.getByText('Data access', { selector: '.pill' })).toBeInTheDocument()       // category "Access"
    expect(screen.getByText('Succeeded', { selector: '.pill', exact: false })).toBeInTheDocument()   // outcome "Success"
  })

  it('sends the cursor for the next page and resets it when a filter changes', async () => {
    const calls = mockFetch((c) => {
      if (c.url.pathname === '/api/producers') return { status: 200, body: [] }
      const actor = c.url.searchParams.get('actorId') ?? 'user:alice'
      return c.url.searchParams.get('cursor')
        ? { status: 200, body: { items: [item(3, actor)], nextCursor: null } }
        : { status: 200, body: { items: [item(1, actor), item(2, actor)], nextCursor: 'cursor-page-2' } }
    })
    renderWithProviders(<EventExplorer />, { url: URL0, path: '/events' })

    expect(await screen.findByText('R-1')).toBeInTheDocument()
    // Only 2 rows are visible, so infinite scroll fetches page 2 by itself ("Load more" is the fallback button).
    expect(await screen.findByText('R-3')).toBeInTheDocument()
    expect(eventCalls(calls)[1].url.searchParams.get('cursor')).toBe('cursor-page-2')

    // Change a filter: the first request for it must start from page 1 (no cursor), and the old rows disappear.
    await userEvent.type(screen.getByLabelText('Who'), 'user:bob')
    await userEvent.click(screen.getByRole('button', { name: 'Search' }))

    await waitFor(() => expect(eventCalls(calls).some((c) => c.url.searchParams.get('actorId') === 'user:bob')).toBe(true))
    const firstForNewFilter = eventCalls(calls).find((c) => c.url.searchParams.get('actorId') === 'user:bob')!
    expect(firstForNewFilter.url.searchParams.get('cursor')).toBeNull()
    await waitFor(() => expect(screen.queryAllByRole('button', { name: 'alice' })).toHaveLength(0))
    expect((await screen.findAllByRole('button', { name: 'bob' })).length).toBeGreaterThan(0)
  })

  it('aborts the in-flight request of the old filter so a stale page never renders', async () => {
    const calls = mockFetch((c) => {
      if (c.url.pathname === '/api/producers') return { status: 200, body: [] }
      if (!c.url.searchParams.get('actorId')) return hangUntilAborted(c.signal)   // slow first search
      return { status: 200, body: { items: [item(9, 'user:new')], nextCursor: null } }
    })
    renderWithProviders(<EventExplorer />, { url: URL0, path: '/events' })
    await waitFor(() => expect(eventCalls(calls).length).toBe(1))

    await userEvent.type(screen.getByLabelText('Who'), 'user:new')
    await userEvent.click(screen.getByRole('button', { name: 'Search' }))

    expect(await screen.findByText('R-9')).toBeInTheDocument()
    await waitFor(() => expect(eventCalls(calls)[0].signal.aborted).toBe(true))
  })

  it('restores filters from the URL and shows API errors with a reference code', async () => {
    mockFetch((c) => c.url.pathname === '/api/producers'
      ? { status: 200, body: [] }
      : { status: 400, body: { code: 'time_range_too_large', detail: 'The time range may be at most 90 days.', correlationId: 'cid-77', errors: { to: ['too large'] } } })
    renderWithProviders(<EventExplorer />, { url: URL0 + '&category=Security', path: '/events' })

    expect(await screen.findByRole('alert')).toHaveTextContent('cid-77')
    expect(screen.getByLabelText('Category')).toHaveValue('Security')                     // URL -> form
    expect(screen.getByText('Category: Security')).toBeInTheDocument()                    // active filter chip
  })
})
