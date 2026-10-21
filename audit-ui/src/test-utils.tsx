import { vi } from 'vitest'
import type { ReactNode } from 'react'
import { render } from '@testing-library/react'
import { QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { createQueryClient } from './api/queries'

export interface Call {
  method: string
  url: URL
  headers: Headers
  signal: AbortSignal
  body: string
}

/**
 * Replaces global fetch with a router: handler(call) returns { status, body } or a Promise of it.
 * Every call is recorded so tests can assert on URLs, headers and abort signals.
 */
export function mockFetch(handler: (call: Call) => { status: number; body: unknown } | Promise<{ status: number; body: unknown }>) {
  const calls: Call[] = []
  vi.stubGlobal('fetch', async (input: Request) => {
    const call: Call = { method: input.method, url: new URL(input.url), headers: input.headers, signal: input.signal, body: await input.clone().text() }
    calls.push(call)
    const { status, body } = await handler(call)
    return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', 'X-Correlation-Id': 'test-cid' } })
  })
  return calls
}

/** Resolves only when aborted (rejecting like fetch does) — simulates a slow request. */
export function hangUntilAborted(signal: AbortSignal): Promise<never> {
  return new Promise((_, reject) => signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError'))))
}

export function renderWithProviders(ui: ReactNode, { url = '/', path = '*' }: { url?: string; path?: string } = {}) {
  const client = createQueryClient()
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[url]}>
        <Routes>
          <Route path={path} element={ui} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}
