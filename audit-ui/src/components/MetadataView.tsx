import { useState } from 'react'
import { humanizeKey } from '../lib/format'

const REDACTED = '[REDACTED]'

interface Row {
  path: string
  label: string
  value: unknown
}

/** { card: { number: 1 } } -> [{ label: "Card › Number", value: 1 }] */
function flatten(value: unknown, path = '', label = ''): Row[] {
  if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
    return Object.entries(value as Record<string, unknown>).flatMap(([k, v]) =>
      flatten(v, path ? `${path}.${k}` : k, label ? `${label} › ${humanizeKey(k)}` : humanizeKey(k)),
    )
  }
  return [{ path, label: label || 'Value', value }]
}

function display(value: unknown): string {
  if (value === null || value === undefined) return '—'
  if (typeof value === 'boolean') return value ? 'Yes' : 'No'
  if (Array.isArray(value)) return value.map((v) => (typeof v === 'object' ? JSON.stringify(v) : String(v))).join(', ')
  return String(value)
}

/**
 * Event "additional details" (metadata).
 * Default: an easy-to-read list ("Card number: Hidden for privacy"). Toggle: the exact JSON.
 *
 * SECURITY: everything is rendered as React text (never dangerouslySetInnerHTML), so a value such as
 * "<script>alert(1)</script>" is shown as literal characters and can never run.
 */
export function MetadataView({ metadata, redactedKeys }: { metadata: unknown; redactedKeys: string[] }) {
  const [asJson, setAsJson] = useState(false)
  const rows = flatten(metadata ?? {})

  return (
    <div className="metadata">
      <div className="metadata-toolbar">
        {redactedKeys.length > 0 && (
          <span className="small">
            <span className="pill tone-private">🔒 {redactedKeys.length} value{redactedKeys.length === 1 ? '' : 's'} hidden for privacy</span>{' '}
            <span className="muted">Sensitive values are masked before they are stored.</span>
          </span>
        )}
        <button type="button" className="link-button" onClick={() => setAsJson((v) => !v)} aria-pressed={asJson}>
          {asJson ? 'Show as list' : 'Show raw JSON'}
        </button>
      </div>

      {asJson ? (
        <pre className="json" aria-label="Event metadata (JSON)">
          {JSON.stringify(metadata ?? {}, null, 2)
            .split('\n')
            .map((line, i) =>
              line.includes(`"${REDACTED}"`) ? (
                <span key={i} className="redacted-line" title="This value was hidden before storage">
                  {line + '   ← hidden for privacy\n'}
                </span>
              ) : (
                <span key={i}>{line + '\n'}</span>
              ),
            )}
        </pre>
      ) : rows.length === 0 ? (
        <p className="muted">No additional details were recorded.</p>
      ) : (
        <dl className="kv" aria-label="Event metadata">
          {rows.map((r) => (
            <div key={r.path} className="kv-row">
              <dt title={r.path}>{r.label}</dt>
              <dd>
                {r.value === REDACTED ? (
                  <span className="pill tone-private" title="This value was hidden before storage">🔒 Hidden for privacy</span>
                ) : (
                  display(r.value)
                )}
              </dd>
            </div>
          ))}
        </dl>
      )}
    </div>
  )
}
