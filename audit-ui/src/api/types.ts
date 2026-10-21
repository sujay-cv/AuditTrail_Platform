import type { components } from './schema'

// Short names for the generated OpenAPI types used across the UI.
type S = components['schemas']

export type Category = S['Category']
export type Outcome = S['Outcome']
export type EventListItem = S['EventListItemDto']
export type EventDetail = S['EventDto']
export type SearchResponse = S['SearchResponse']
export type VerifyResult = S['VerifyResultDto']
export type ExportJob = S['ExportJobDto']
export type ExportStatus = S['ExportStatus']
export type RetentionPolicy = S['RetentionPolicyDto']
export type LegalHold = S['LegalHoldDto']
export type HoldScopeType = S['HoldScopeType']
export type Summary = S['SummaryDto']
export type Producer = S['ProducerDto']
export type Tenant = S['TenantDto']

export const CATEGORIES: Category[] = ['Security', 'Access', 'DataChange', 'Configuration', 'Financial']
export const OUTCOMES: Outcome[] = ['Success', 'Failure', 'Denied']
export const TERMINAL_EXPORT_STATUSES: ExportStatus[] = ['Completed', 'Failed', 'Cancelled', 'Expired']
