import { QueryClient, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, call, ApiError } from './client'
import { TERMINAL_EXPORT_STATUSES, type Category, type HoldScopeType } from './types'
import type { EventFilters } from '../lib/filters'
import { useSession } from '../session/useSession'

/**
 * Server state lives in TanStack Query:
 *  - caching: staleTime 30s; every key starts with the tenant + role, so data never leaks between tenants.
 *  - cancellation: each queryFn passes `signal` to fetch; when the key changes (e.g. a filter changes) the old
 *    request is aborted and its result is never rendered.
 *  - retries: only for READS (queries) and only for transient failures (network / 503). Mutations never auto-retry.
 */
export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 30_000,
        refetchOnWindowFocus: false,
        retry: (failureCount, error) =>
          failureCount < 2 && error instanceof ApiError && (error.status === 0 || error.status === 503),
      },
      mutations: { retry: false },
    },
  })
}

function useScope() {
  const [session] = useSession()
  return [session.tenantId, session.role] as const
}

export const PAGE_SIZE = 100
/** Hard cap on rows kept in the Explorer (virtualized list stays smooth well beyond this). */
export const MAX_LOADED_ROWS = 10_000

// ---------------- Tenant / producers ----------------

export function useTenants() {
  return useQuery({
    queryKey: ['tenants'],
    queryFn: ({ signal }) => call(api.GET('/api/tenants', { signal })),
    staleTime: Infinity,
  })
}

export function useProducers() {
  const scope = useScope()
  return useQuery({
    queryKey: [...scope, 'producers'],
    queryFn: ({ signal }) => call(api.GET('/api/producers', { signal })),
    staleTime: 5 * 60_000,
  })
}

// ---------------- Events ----------------

export function useEventSearch(filters: EventFilters | null) {
  const scope = useScope()
  return useInfiniteQuery({
    queryKey: [...scope, 'events', filters],
    enabled: filters !== null,
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam, signal }) => call(api.GET('/api/events', {
      params: { query: { ...filters!, newestFirst: true, cursor: pageParam, limit: PAGE_SIZE } },
      signal,
    })),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  })
}

export function useEvent(id: string) {
  const scope = useScope()
  return useQuery({
    queryKey: [...scope, 'event', id],
    queryFn: ({ signal }) => call(api.GET('/api/events/{id}', { params: { path: { id } }, signal })),
    staleTime: Infinity, // events are immutable
  })
}

// ---------------- Integrity ----------------

export function useVerify() {
  return useMutation({
    mutationFn: (range: { fromSequence?: number; toSequence?: number }) =>
      call(api.POST('/api/integrity/verify', { body: { fromSequence: range.fromSequence ?? null, toSequence: range.toSequence ?? null } })),
  })
}

// ---------------- Exports ----------------

export function useExports() {
  const scope = useScope()
  return useQuery({
    queryKey: [...scope, 'exports'],
    queryFn: ({ signal }) => call(api.GET('/api/exports', { signal })),
    // Live progress: poll every 1.5s while any job is still Queued/Running, otherwise stop polling.
    refetchInterval: (query) =>
      query.state.data?.some((j) => !TERMINAL_EXPORT_STATUSES.includes(j.status)) ? 1500 : false,
    staleTime: 0,
  })
}

export function useCreateExport() {
  const client = useQueryClient()
  const scope = useScope()
  return useMutation({
    mutationFn: ({ filters, idempotencyKey }: { filters: EventFilters; idempotencyKey: string }) =>
      call(api.POST('/api/exports', {
        params: { header: { 'Idempotency-Key': idempotencyKey } },
        body: {
          filter: {
            from: filters.from, to: filters.to,
            actorId: filters.actorId ?? null, actionPrefix: filters.actionPrefix ?? null,
            resourceType: filters.resourceType ?? null, resourceId: filters.resourceId ?? null,
            outcome: filters.outcome ?? null, category: filters.category ?? null, producerId: filters.producerId ?? null,
          },
          format: 'Ndjson',
        },
      })),
    onSuccess: () => client.invalidateQueries({ queryKey: [...scope, 'exports'] }),
  })
}

export function useCancelExport() {
  const client = useQueryClient()
  const scope = useScope()
  return useMutation({
    mutationFn: (id: string) => call(api.POST('/api/exports/{id}/cancel', { params: { path: { id } } })),
    onSettled: () => client.invalidateQueries({ queryKey: [...scope, 'exports'] }),
  })
}

// ---------------- Retention & legal holds ----------------

export function usePolicies() {
  const scope = useScope()
  return useQuery({
    queryKey: [...scope, 'policies'],
    queryFn: ({ signal }) => call(api.GET('/api/retention/policies', { signal })),
  })
}

export function useHolds() {
  const scope = useScope()
  return useQuery({
    queryKey: [...scope, 'holds'],
    queryFn: ({ signal }) => call(api.GET('/api/legal-holds', { signal })),
  })
}

function useInvalidateRetention() {
  const client = useQueryClient()
  const scope = useScope()
  return () => {
    client.invalidateQueries({ queryKey: [...scope, 'policies'] })
    client.invalidateQueries({ queryKey: [...scope, 'holds'] })
    client.invalidateQueries({ queryKey: [...scope, 'summary'] })
  }
}

export function useUpdatePolicy() {
  const invalidate = useInvalidateRetention()
  return useMutation({
    mutationFn: ({ category, retentionDays }: { category: Category; retentionDays: number }) =>
      call(api.PUT('/api/retention/policies/{category}', { params: { path: { category } }, body: { retentionDays } })),
    onSuccess: invalidate,
  })
}

export function useCreateHold() {
  const invalidate = useInvalidateRetention()
  return useMutation({
    mutationFn: (body: { scopeType: HoldScopeType; scopeValue: string; reason: string }) =>
      call(api.POST('/api/legal-holds', { body })),
    onSuccess: invalidate,
  })
}

export function useReleaseHold() {
  const invalidate = useInvalidateRetention()
  return useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) =>
      call(api.POST('/api/legal-holds/{id}/release', { params: { path: { id } }, body: { reason } })),
    onSuccess: invalidate,
  })
}

export function useRunRetention() {
  const client = useQueryClient()
  const invalidate = useInvalidateRetention()
  return useMutation({
    mutationFn: () => call(api.POST('/api/retention/run')),
    onSuccess: () => {
      invalidate()
      client.invalidateQueries({ predicate: (q) => q.queryKey.includes('events') })
    },
  })
}

// ---------------- Summary ----------------

export function useSummary(from: string, to: string) {
  const scope = useScope()
  return useQuery({
    queryKey: [...scope, 'summary', from, to],
    queryFn: ({ signal }) => call(api.GET('/api/summary', { params: { query: { from, to } }, signal })),
  })
}
