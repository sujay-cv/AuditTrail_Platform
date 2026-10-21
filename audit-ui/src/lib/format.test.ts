import { describe, expect, it } from 'vitest'
import { actorName, humanDays, humanizeAction, humanizeKey, humanSeconds, relativeTime } from './format'

describe('plain-language formatting', () => {
  it('turns technical values into words', () => {
    expect(humanizeAction('payment.approved')).toBe('Payment approved')
    expect(humanizeAction('user.role.granted')).toBe('User role granted')
    expect(actorName('user:jdoe')).toBe('jdoe')
    expect(actorName('svc:payments-service')).toBe('payments-service')
    expect(actorName('plain')).toBe('plain')
    expect(humanizeKey('cardNumber')).toBe('Card number')
    expect(humanizeKey('changed_fields')).toBe('Changed fields')
  })

  it('describes durations the way people say them', () => {
    expect(humanDays(2555)).toBe('7 years')
    expect(humanDays(365)).toBe('1 year')
    expect(humanDays(90)).toBe('3 months')
    expect(humanDays(45)).toBe('45 days')
    expect(humanDays(400)).toBe('about 1 year')
    expect(humanSeconds(12)).toBe('12 seconds')
    expect(humanSeconds(300)).toBe('5.0 minutes')
    expect(humanSeconds(null)).toBe('—')
  })

  it('shows relative times', () => {
    const now = Date.parse('2026-10-21T12:00:00Z')
    expect(relativeTime('2026-10-21T11:59:50Z', now)).toBe('just now')
    expect(relativeTime('2026-10-21T09:00:00Z', now)).toMatch(/3 hours ago/)
  })
})
