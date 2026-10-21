import type { Category, ExportStatus, Outcome } from '../api/types'

/**
 * Turns technical values from the API into words people understand.
 * Only DISPLAY changes here: the API and the URL keep the exact values.
 */

export const categoryLabel: Record<Category, string> = {
  Security: 'Security',
  Access: 'Data access',
  DataChange: 'Data change',
  Configuration: 'Settings change',
  Financial: 'Financial',
}

export const categoryHelp: Record<Category, string> = {
  Security: 'Sign-ins, sign-outs, password and MFA activity',
  Access: 'Someone viewed or downloaded information',
  DataChange: 'Records were created, updated or deleted',
  Configuration: 'Permissions, roles or system settings were changed',
  Financial: 'Payments, refunds and transfers',
}

export const outcomeLabel: Record<Outcome, string> = {
  Success: 'Succeeded',
  Failure: 'Failed',
  Denied: 'Denied',
}

export const outcomeIcon: Record<Outcome, string> = {
  Success: '✓',
  Failure: '!',
  Denied: '✕',
}

export const exportStatusLabel: Record<ExportStatus, string> = {
  Queued: 'Waiting to start',
  Running: 'In progress',
  Completed: 'Ready to download',
  Failed: 'Failed',
  Cancelled: 'Cancelled',
  Expired: 'Expired',
}

export const actorTypeLabel: Record<string, string> = {
  User: 'Person',
  Service: 'Application',
  System: 'System',
}

/** "payment.approved" -> "Payment approved", "user.role.granted" -> "User role granted" */
export function humanizeAction(action: string): string {
  const words = action.split(/[._-]+/).filter(Boolean).join(' ')
  return words.charAt(0).toUpperCase() + words.slice(1)
}

const systemNames: Record<string, string> = {
  'hr-service': 'Human Resources',
  'admin-console': 'Administration Console',
  'audit-platform': 'Audit Trail Platform',
  'legacy-batch': 'Legacy Import Service',
}

/** Presentation only: keep the original identifier in API requests and audit details. */
export function displayName(value: string): string {
  if (Object.hasOwn(systemNames, value)) return systemNames[value]
  return value.replace(/[._-]+/g, ' ').replace(/\b[a-z]/g, (letter) => letter.toUpperCase())
}

// Fictional display names for this project's existing demo fixtures, not an identity directory.
const demoPeople: Record<string, string> = {
  'user:jdoe': 'John Doe',
  'user:asmith': 'Anna Smith',
  'user:mkhan': 'Maya Khan',
  'user:lchen': 'Lily Chen',
  'user:rpatel': 'Riya Patel',
  'user:okafor': 'Olivia Okafor',
  'user:mallory': 'Mallory Reed',
}

const demoSimulatorPeople = [
  'Alex Carter', 'Priya Sharma', 'Daniel Brooks', 'Sofia Garcia', 'James Wilson',
  'Emma Taylor', 'Noah Williams', 'Ava Johnson', 'Liam Brown', 'Mia Davis',
  'Ethan Miller', 'Isabella Moore', 'Lucas Martin', 'Amelia Thompson', 'Oliver White',
  'Charlotte Harris', 'Benjamin Clark', 'Harper Lewis', 'Henry Walker', 'Evelyn Hall',
  'Samuel Allen', 'Abigail Young', 'Jack King', 'Emily Wright', 'Leo Scott',
  'Grace Green', 'Arjun Mehta', 'Anika Rao', 'Owen Baker', 'Chloe Adams',
  'Isaac Nelson', 'Zoe Hill', 'Adam Mitchell', 'Hannah Roberts', 'Ryan Turner',
  'Nora Phillips', 'David Campbell', 'Layla Parker', 'Aaron Evans',
]

/** Demo aliases get fictional names; other identifiers are formatted without guessing a person's identity. */
export function actorName(actorId: string): string {
  if (Object.hasOwn(demoPeople, actorId)) return demoPeople[actorId]
  const simulator = /^user:sim([1-9]\d*)$/.exec(actorId)
  if (simulator) {
    const name = demoSimulatorPeople[Number(simulator[1]) - 1]
    if (name) return name
  }
  const i = actorId.indexOf(':')
  return displayName(i > 0 && i < actorId.length - 1 ? actorId.slice(i + 1) : actorId)
}

/** "cardNumber" -> "Card number", "changed_fields" -> "Changed fields" */
export function humanizeKey(key: string): string {
  const words = key
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_\-.]+/g, ' ')
    .trim()
    .toLowerCase()
  return words.charAt(0).toUpperCase() + words.slice(1)
}

/** 2555 -> "7 years", 365 -> "1 year", 90 -> "3 months", 45 -> "45 days" */
export function humanDays(days: number): string {
  const plural = (n: number, unit: string) => `${n.toLocaleString()} ${unit}${n === 1 ? '' : 's'}`
  if (days >= 365 && days % 365 === 0) return plural(days / 365, 'year')
  if (days >= 365) return `about ${plural(Math.round(days / 365), 'year')}`
  if (days >= 30 && days % 30 === 0) return plural(days / 30, 'month')
  return plural(days, 'day')
}

/** Seconds -> "12 seconds", "3.5 minutes", "2.1 hours" */
export function humanSeconds(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined) return '—'
  if (seconds < 90) return `${Math.round(seconds)} second${Math.round(seconds) === 1 ? '' : 's'}`
  if (seconds < 5400) return `${(seconds / 60).toFixed(1)} minutes`
  if (seconds < 172_800) return `${(seconds / 3600).toFixed(1)} hours`
  return `${(seconds / 86_400).toFixed(1)} days`
}

const relative = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })

/** "3 hours ago", "yesterday", "in 2 minutes" */
export function relativeTime(iso: string, now = Date.now()): string {
  const seconds = Math.round((Date.parse(iso) - now) / 1000)
  const abs = Math.abs(seconds)
  if (abs < 45) return 'just now'
  if (abs < 3600) return relative.format(Math.round(seconds / 60), 'minute')
  if (abs < 86_400) return relative.format(Math.round(seconds / 3600), 'hour')
  if (abs < 30 * 86_400) return relative.format(Math.round(seconds / 86_400), 'day')
  if (abs < 365 * 86_400) return relative.format(Math.round(seconds / (30 * 86_400)), 'month')
  return relative.format(Math.round(seconds / (365 * 86_400)), 'year')
}

const shortDate = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short', year: 'numeric' })

export function formatDate(iso: string): string {
  return shortDate.format(new Date(iso))
}

/** Reason codes from the tamper check -> a sentence. */
export const breakReason: Record<string, string> = {
  content_modified: 'was changed after it was saved',
  missing: 'is missing — it was deleted outside the normal clean-up process',
  prev_hash_mismatch: 'is out of order, or its link to the record before it is broken',
  head_mismatch: 'does not match the last record the system knows about',
  unknown_hash_version: 'was sealed with an unknown method',
}
