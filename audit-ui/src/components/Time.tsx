import { formatLocal, formatUtc } from '../lib/time'
import { relativeTime } from '../lib/format'

/**
 * Local date/time on screen; UTC on hover (title). With `relative`, shows "3 hours ago" with the exact time underneath.
 */
export function Time({ iso, relative = false }: { iso: string | null | undefined; relative?: boolean }) {
  if (!iso) return <span className="muted">—</span>
  const title = `${formatLocal(iso)} (your time) · ${formatUtc(iso)}`
  if (relative)
    return (
      <time dateTime={iso} title={title} className="time-stack">
        <span>{relativeTime(iso)}</span>
        <span className="muted small">{formatLocal(iso)}</span>
      </time>
    )
  return (
    <time dateTime={iso} title={title}>
      {formatLocal(iso)}
    </time>
  )
}
