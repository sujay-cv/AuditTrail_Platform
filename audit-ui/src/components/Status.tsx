import type { ReactNode } from 'react'
import type { Category, ExportStatus, Outcome } from '../api/types'
import { categoryHelp, categoryLabel, exportStatusLabel, outcomeIcon, outcomeLabel } from '../lib/format'

export function OutcomeBadge({ value }: { value: Outcome }) {
  return (
    <span className={`pill outcome-${value.toLowerCase()}`}>
      <span aria-hidden="true">{outcomeIcon[value]}</span> {outcomeLabel[value]}
    </span>
  )
}

export function CategoryBadge({ value }: { value: Category }) {
  return (
    <span className={`pill category-${value.toLowerCase()}`} title={categoryHelp[value]}>
      {categoryLabel[value]}
    </span>
  )
}

export function ExportStatusBadge({ value }: { value: ExportStatus }) {
  return <span className={`pill status-${value.toLowerCase()}`}>{exportStatusLabel[value]}</span>
}

export function Loading({ label = 'Loading…' }: { label?: string }) {
  return (
    <div className="loading" role="status" aria-live="polite">
      <span className="spinner" aria-hidden="true" /> {label}
    </div>
  )
}

export function Empty({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="empty-state">
      <div className="empty-icon" aria-hidden="true">🔍</div>
      <strong>{title}</strong>
      {children && <p className="muted">{children}</p>}
    </div>
  )
}

export function PageHeader({ title, subtitle, actions }: { title: string; subtitle?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="page-header">
      <div>
        <h1>{title}</h1>
        {subtitle && <p className="subtitle">{subtitle}</p>}
      </div>
      {actions && <div className="page-actions">{actions}</div>}
    </div>
  )
}
