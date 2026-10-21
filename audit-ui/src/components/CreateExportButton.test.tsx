import { afterEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { CreateExportButton } from './CreateExportButton'
import { mockFetch, renderWithProviders } from '../test-utils'

const filters = { from: '2026-09-01T00:00:00.000Z', to: '2026-10-01T00:00:00.000Z', category: 'Financial' as const }
const job = {
  id: 'job-1', status: 'Queued', format: 'Ndjson', filter: { ...filters }, requestedBy: 'auditor', attempts: 0, rowCount: 0,
  snapshotSequence: 10, currentCursor: null, checksum: null, error: null, createdAt: '2026-10-01T00:00:00Z', completedAt: null, leaseExpiresAt: null,
}

afterEach(() => vi.unstubAllGlobals())

describe('CreateExportButton', () => {
  it('a double click creates ONE export with ONE idempotency key', async () => {
    const calls = mockFetch(async () => {
      await new Promise((r) => setTimeout(r, 50))
      return { status: 202, body: job }
    })
    renderWithProviders(<CreateExportButton filters={filters} />)

    await userEvent.dblClick(screen.getByRole('button', { name: /export results/i }))
    await screen.findByText(/export started/i)

    const keys = new Set(calls.filter((c) => c.method === 'POST').map((c) => c.headers.get('Idempotency-Key')))
    expect(keys.size).toBe(1)
    expect([...keys][0]).toMatch(/^[0-9a-f-]{36}$/)
  })

  it('a retry after a failure reuses the key (same intent); a new click after success uses a new key', async () => {
    let attempt = 0
    const calls = mockFetch(() => (++attempt === 1
      ? { status: 503, body: { code: 'service_unavailable', detail: 'Database down', correlationId: 'cid-1' } }
      : { status: 202, body: job }))
    renderWithProviders(<CreateExportButton filters={filters} />)
    const button = screen.getByRole('button', { name: /export results/i })

    await userEvent.click(button)
    expect(await screen.findByRole('alert')).toHaveTextContent('cid-1')   // friendly error with the reference code

    await userEvent.click(button)                                          // the user retries
    await screen.findByText(/export started/i)
    await userEvent.click(button)                                          // a new export request
    await waitFor(() => expect(calls.length).toBe(3))

    const [k1, k2, k3] = calls.map((c) => c.headers.get('Idempotency-Key'))
    expect(k2).toBe(k1)
    expect(k3).not.toBe(k1)
  })
})
