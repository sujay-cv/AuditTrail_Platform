import { describe, expect, it } from 'vitest'
import { actorName, displayName, humanDays, humanizeAction, humanizeKey, humanSeconds, relativeTime } from './format'

describe('plain-language formatting', () => {
  it('turns technical values into words', () => {
    expect(humanizeAction('payment.approved')).toBe('Payment approved')
    expect(humanizeAction('user.role.granted')).toBe('User role granted')
    expect(actorName('user:jdoe')).toBe('John Doe')
    expect(actorName('svc:payments-service')).toBe('Payments Service')
    expect(actorName('plain')).toBe('Plain')
    expect(actorName('user:alice.smith')).toBe('Alice Smith')
    expect(displayName('identity-service')).toBe('Identity Service')
    expect(displayName('HR Console')).toBe('HR Console')
    expect(actorName('')).toBe('')
  })

  it('gives existing demo people and systems meaningful names without changing other IDs', () => {
    expect(actorName('user:asmith')).toBe('Anna Smith')
    expect(actorName('user:mkhan')).toBe('Maya Khan')
    expect(actorName('user:mallory')).toBe('Mallory Reed')
    expect(actorName('user:sim1')).toBe('Alex Carter')
    expect(actorName('user:sim32')).toBe('Zoe Hill')
    expect(actorName('user:sim39')).toBe('Aaron Evans')
    expect(actorName('user:sim40')).toBe('Sim40')
    expect(actorName('user:unknown-person')).toBe('Unknown Person')
    expect(displayName('hr-service')).toBe('Human Resources')
    expect(displayName('audit-platform')).toBe('Audit Trail Platform')
    expect(actorName('constructor')).toBe('Constructor')
    expect(displayName('toString')).toBe('ToString')
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
