/** Display in the user's time zone; UTC is shown on hover (see components/Time.tsx). The API only sees UTC ISO strings. */

const localFormat = new Intl.DateTimeFormat(undefined, {
  year: 'numeric', month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit',
})

export function formatLocal(iso: string): string {
  return localFormat.format(new Date(iso))
}

export function formatUtc(iso: string): string {
  return new Date(iso).toISOString().replace('T', ' ').replace(/\.\d+Z$/, ' UTC')
}

/** UTC ISO -> value for <input type="datetime-local"> (local time, minute precision). */
export function toLocalInput(iso: string): string {
  const d = new Date(iso)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

/** <input type="datetime-local"> value (local time) -> UTC ISO. */
export function fromLocalInput(value: string): string | undefined {
  const d = new Date(value)
  return Number.isNaN(d.getTime()) ? undefined : d.toISOString()
}

export function daysAgoIso(days: number, now = new Date()): string {
  return new Date(now.getTime() - days * 86_400_000).toISOString()
}
