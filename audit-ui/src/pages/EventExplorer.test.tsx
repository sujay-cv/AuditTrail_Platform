import { afterEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { EventExplorer } from './EventExplorer'
import { toLocalInput } from '../lib/time'
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
    expect(screen.getByRole('button', { name: 'Alice' })).toBeInTheDocument()                // "user:alice"
    expect(screen.getByText('Producer 1')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Search' })).not.toBeInTheDocument()
    expect(within(screen.getByRole('table', { name: 'Activity records' })).getByLabelText('Category')).toBeInTheDocument()
    expect(screen.getByText('Data access', { selector: '.pill' })).toBeInTheDocument()       // category "Access"
    expect(screen.getByText('Succeeded', { selector: '.pill', exact: false })).toBeInTheDocument()   // outcome "Success"
  })

  it('shows a meaningful demo name but filters using the unchanged original ID', async () => {
    const calls = mockFetch((c) => c.url.pathname === '/api/producers'
      ? { status: 200, body: [] }
      : { status: 200, body: { items: [item(1, 'user:jdoe')], nextCursor: null } })
    renderWithProviders(<EventExplorer />, { url: URL0, path: '/events' })
    await userEvent.click(await screen.findByRole('button', { name: 'John Doe' }))
    await waitFor(() => expect(eventCalls(calls).at(-1)!.url.searchParams.get('actorId')).toBe('user:jdoe'))
    expect(screen.getByLabelText('Who (exact ID)')).toHaveValue('user:jdoe')
    expect(screen.getByText('Who: John Doe')).toBeInTheDocument()
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
    expect(eventCalls(calls)).toHaveLength(1)
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }))
    expect(await screen.findByText('R-3')).toBeInTheDocument()
    expect(eventCalls(calls)[1].url.searchParams.get('cursor')).toBe('cursor-page-2')

    // Change a filter: the first request for it must start from page 1 (no cursor), and the old rows disappear.
    await userEvent.type(screen.getByLabelText('Who (exact ID)'), 'user:bob{Enter}')

    await waitFor(() => expect(eventCalls(calls).some((c) => c.url.searchParams.get('actorId') === 'user:bob')).toBe(true))
    const firstForNewFilter = eventCalls(calls).find((c) => c.url.searchParams.get('actorId') === 'user:bob')!
    expect(firstForNewFilter.url.searchParams.get('cursor')).toBeNull()
    await waitFor(() => expect(screen.queryAllByRole('button', { name: 'Alice' })).toHaveLength(0))
    expect((await screen.findAllByRole('button', { name: 'Bob' })).length).toBeGreaterThan(0)
  })

  it('aborts the in-flight request of the old filter so a stale page never renders', async () => {
    const calls = mockFetch((c) => {
      if (c.url.pathname === '/api/producers') return { status: 200, body: [] }
      if (!c.url.searchParams.get('actorId')) return hangUntilAborted(c.signal)   // slow first search
      return { status: 200, body: { items: [item(9, 'user:new')], nextCursor: null } }
    })
    renderWithProviders(<EventExplorer />, { url: URL0, path: '/events' })
    await waitFor(() => expect(eventCalls(calls).length).toBe(1))

    await userEvent.type(screen.getByLabelText('Who (exact ID)'), 'user:new{Enter}')

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

  it('keeps filters usable with zero results and combines text with dropdown filters', async () => {
    const calls = mockFetch((c) => c.url.pathname === '/api/producers'
      ? { status: 200, body: [{ id: 'p1', name: 'payments-service' }] }
      : { status: 200, body: { items: [], nextCursor: null } })
    renderWithProviders(<EventExplorer />, { url: URL0, path: '/events' })
    expect(await screen.findByText('No activity found')).toBeInTheDocument()

    await userEvent.type(screen.getByLabelText('Who (exact ID)'), 'user:alice')
    await userEvent.tab()
    await userEvent.selectOptions(screen.getByLabelText('Category'), 'Financial')
    await userEvent.selectOptions(screen.getByLabelText('Source system'), 'p1')
    await waitFor(() => {
      const query = eventCalls(calls).at(-1)!.url.searchParams
      expect(query.get('actorId')).toBe('user:alice')
      expect(query.get('category')).toBe('Financial')
      expect(query.get('producerId')).toBe('p1')
    })
    expect(screen.getByText('Who: Alice')).toBeInTheDocument()
    expect(screen.getByText('Source: Payments Service')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Clear all' }))
    await waitFor(() => expect(screen.getByLabelText('Who (exact ID)')).toHaveValue(''))
    expect(screen.getByLabelText('Category')).toHaveValue('')
    expect(screen.getByLabelText('Source system')).toHaveValue('')
  })

  it('applies custom dates without losing column filters and rejects ranges over 90 days', async () => {
    const calls = mockFetch((c) => c.url.pathname === '/api/producers'
      ? { status: 200, body: [] }
      : { status: 200, body: { items: [], nextCursor: null } })
    renderWithProviders(<EventExplorer />, { url: URL0, path: '/events' })
    await screen.findByText('No activity found')
    await userEvent.selectOptions(screen.getByLabelText('Category'), 'Security')
    await userEvent.click(screen.getByRole('button', { name: 'Apply dates' }))
    expect(screen.getByLabelText('Category')).toHaveValue('Security')
    const before = eventCalls(calls).length
    fireEvent.change(screen.getByLabelText('From'), { target: { value: '2026-01-01T00:00' } })
    await userEvent.click(screen.getByRole('button', { name: 'Apply dates' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('90 days or less')
    expect(eventCalls(calls)).toHaveLength(before)
  })

  it('keeps the table filters available when source systems fail to load', async () => {
    mockFetch((c) => c.url.pathname === '/api/producers'
      ? { status: 400, body: { detail: 'Sources unavailable', correlationId: 'source-1' } }
      : { status: 200, body: { items: [], nextCursor: null } })
    renderWithProviders(<EventExplorer />, { url: URL0, path: '/events' })
    expect(await screen.findByRole('alert')).toHaveTextContent('source-1')
    expect(screen.getByLabelText('Source system')).toBeDisabled()
    expect(screen.getByLabelText('Category')).toBeEnabled()
  })

  it.each([
    { days: 90, error: undefined },
    { days: 90 + 1 / 1440, error: '90 days or less' },
    { days: 0, error: 'end date must be after' },
    { days: -1, error: 'end date must be after' },
  ])('validates a custom period of $days days', async ({ days, error }) => {
    const calls = mockFetch((c) => c.url.pathname === '/api/producers'
      ? { status: 200, body: [] }
      : { status: 200, body: { items: [], nextCursor: null } })
    renderWithProviders(<EventExplorer />, { url: URL0, path: '/events' })
    await screen.findByText('No activity found')
    const from = Date.parse('2026-01-01T00:00:00Z')
    const to = new Date(from + days * 86_400_000).toISOString()
    fireEvent.change(screen.getByLabelText('From'), { target: { value: toLocalInput(new Date(from).toISOString()) } })
    fireEvent.change(screen.getByLabelText('To'), { target: { value: toLocalInput(to) } })
    const before = eventCalls(calls).length
    await userEvent.click(screen.getByRole('button', { name: 'Apply dates' }))
    if (error) {
      expect(await screen.findByRole('alert')).toHaveTextContent(error)
      expect(eventCalls(calls)).toHaveLength(before)
    } else {
      await waitFor(() => expect(eventCalls(calls).at(-1)!.url.searchParams.get('to')).toBe(to))
      expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    }
  })
})
