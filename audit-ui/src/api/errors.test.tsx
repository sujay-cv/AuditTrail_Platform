import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { toApiError } from './errors'
import { ErrorPanel } from '../components/ErrorPanel'

describe('ProblemDetails mapping', () => {
  const problem = {
    type: 'urn:audit-trail:error:time_range_too_large',
    title: 'Bad Request',
    status: 400,
    detail: 'The time range may be at most 90 days.',
    code: 'time_range_too_large',
    correlationId: 'abc123',
    errors: { to: ['The time range may be at most 90 days.'] },
  }

  it('maps code, message, field errors and correlation id', () => {
    const e = toApiError(400, problem, 'header-id')
    expect(e.status).toBe(400)
    expect(e.code).toBe('time_range_too_large')
    expect(e.message).toBe('The time range may be at most 90 days.')
    expect(e.correlationId).toBe('abc123')           // body wins over header
    expect(e.field('to')).toBe('The time range may be at most 90 days.')
  })

  it('keeps extension members such as currentStatus', () => {
    const e = toApiError(409, { code: 'export_not_cancellable', detail: 'x', currentStatus: 'Completed' }, null)
    expect(e.extensions.currentStatus).toBe('Completed')
  })

  it('falls back to a friendly message and the header correlation id for non-JSON bodies', () => {
    const e = toApiError(503, '<html>Bad gateway</html>', 'from-header')
    expect(e.message).toMatch(/temporarily unavailable/)
    expect(e.correlationId).toBe('from-header')
    expect(e.code).toBe('http_503')
  })

  it('never shows raw server details such as stack traces', () => {
    const e = toApiError(500, { stackTrace: 'at Foo.Bar() in Secret.cs:line 42', code: 'internal_error' }, 'cid-1')
    render(<ErrorPanel error={e} />)
    expect(screen.getByRole('alert')).toHaveTextContent('Something went wrong on our side')
    expect(screen.getByRole('alert')).toHaveTextContent('cid-1')
    expect(screen.getByRole('alert')).not.toHaveTextContent('Secret.cs')
  })

  it('renders a 403 as "Not allowed" with the correlation id', () => {
    render(<ErrorPanel error={toApiError(403, { code: 'forbidden', detail: 'Your role is not allowed to perform this action.', correlationId: 'cid-403' }, null)} />)
    expect(screen.getByRole('alert')).toHaveTextContent('Not allowed')
    expect(screen.getByRole('alert')).toHaveTextContent('cid-403')
  })
})
