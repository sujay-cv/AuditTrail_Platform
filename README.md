# Audit Trail Platform

A multi-tenant, **tamper-evident audit trail**. Business systems ("producers") send records of *who did what, to
which item, and whether it worked*. The platform stores each record exactly once, seals it into a per-tenant hash
chain, and lets auditors search, verify, export and manage retention through a web console.

| Part | Tech | Folder |
|---|---|---|
| API + background workers | ASP.NET Core (.NET 10), Controllers, Dapper + Npgsql | [AuditTrail.Api](AuditTrail.Api) |
| Database | PostgreSQL 16 (plain SQL migrations) | [AuditTrail.Api/Migrations](AuditTrail.Api/Migrations) |
| Auditor console | React 19 + TypeScript + Vite, TanStack Query | [audit-ui](audit-ui) |
| Producer simulator | .NET console app | [AuditTrail.Simulator](AuditTrail.Simulator) |
| Tests | xUnit + Testcontainers (real PostgreSQL), Vitest | [AuditTrail.Tests](AuditTrail.Tests), `audit-ui/src/**/*.test.tsx` |

---

## 1. Run it

### Option A — everything in Docker (one command)

```powershell
copy .env.example .env          # optional: every setting has a safe default
docker compose up -d --build
```

| What | URL |
|---|---|
| Auditor console | http://localhost:3000 |
| API + Swagger UI | http://localhost:8080/swagger |
| Liveness / readiness | http://localhost:8080/health/live · http://localhost:8080/health/ready |

The API applies migrations and seeds demo data on first start (two organizations, ~1,450 events over 150 days).

### Option B — API in Visual Studio, UI in VS Code (development)

1. **Database only:** `docker compose up -d postgres`
2. **API:** open [AuditTrail.slnx](AuditTrail.slnx) in Visual Studio → set **AuditTrail.Api** as startup project → **F5**.
   It runs on http://localhost:8080 and opens Swagger. Settings come from
   [appsettings.json](AuditTrail.Api/appsettings.json) + [appsettings.Development.json](AuditTrail.Api/appsettings.Development.json).
   Use [AuditTrail.Api.http](AuditTrail.Api/AuditTrail.Api.http) to send example requests from the editor.
3. **UI (hot reload):** open the `audit-ui` folder in VS Code:
   ```powershell
   npm install
   npm run dev          # http://localhost:5173
   ```
   The API URL is in [public/config.js](audit-ui/public/config.js) (in Docker it is generated from `API_BASE_URL`).

> If Docker's `api`/`ui` containers are running, stop them first (`docker compose stop api ui`) so ports 8080/3000 are free.

### Producer simulator (optional)

```powershell
dotnet run --project AuditTrail.Simulator -- --tenants 2 --producers 6 --events 20000 --batch-size 100 `
  --concurrency 32 --duplicates 5 --late 5 --invalid 2 --retry-timeouts --hot-tenant 60 --base-url http://localhost:8080
# or: docker compose --profile simulator up simulator
```

It prints events sent / accepted / duplicates / rejected by reason / retries / throughput / p95 latency.
Afterwards, run the **Tamper check** in the console: the chain is still gap-free and verifies.

### Demo script (5 minutes)

1. **Activity log** → open the record by `mallory` (yesterday): the `<script>` text is shown harmlessly; card number and
   password show "Hidden for privacy".
2. Switch **Organization** to Globex → search `evt-shared-0001`'s action (`payment`): the same eventId exists in both
   organizations, each sees only its own.
3. **Export results** (double-click it) → **Exports** shows one job with live progress. Kill the worker mid-export and restart:
   ```powershell
   docker compose kill api; docker compose start api
   ```
   The job resumes ("resumed after an interruption") and completes; **Download file** shows "✓ verified".
4. **Tamper check** → all good. Run `scripts\tamper.sql` (section 3.3) → run again → record #100 flagged, with a link.
5. Sign in as **Compliance admin** → **Retention & holds** → place a hold → **Run clean-up now** (Acme keeps Access records
   90 days, so old ones are deleted; `user:jdoe`'s are protected by the seeded hold). Switch to Auditor: everything is read-only.
6. **Dashboard** → "See the platform's own records": every export, hold and retention change is itself audited.
7. Database outage: `docker compose stop postgres` → the sidebar shows "Service unavailable", requests show a clear error
   with a reference code, `/health/ready` returns 503 while `/health/live` stays 200, workers back off.
   `docker compose start postgres` → everything recovers on its own.

### Tests

```powershell
dotnet test                          # 31 tests; needs Docker (Testcontainers starts its own PostgreSQL)
cd audit-ui; npm test                # 21 UI tests (Vitest + Testing Library)
```

### Look at the data with a GUI

Connect any PostgreSQL GUI (pgAdmin, DBeaver, Azure Data Studio…) to `localhost:5432`, database `audittrail`,
user `audit`, password `audit_dev_password` (local demo values from `.env.example`).
Or start pgAdmin in Docker: `docker compose --profile tools up -d pgadmin` → http://localhost:5050.

### Demo logins (stand-in for SSO)

The console's top-right pickers choose the **organization** and **role** (Auditor = view only, Compliance admin = can
change retention and legal holds). Producer API keys used by the simulator and `.http` file: `acme-payments-key`,
`acme-identity-key`, `acme-hr-key`, `acme-admin-key`, `globex-claims-key`, … and the revoked `acme-revoked-key`.

---

## 2. How the code is organized

```
AuditTrail.Api/
  Program.cs            DI registrations, auth, CORS, error handling, health checks, OpenAPI, startup migration
  appsettings*.json     every setting (connection string incl. pool/timeouts, batch limits, worker intervals…)
  Controllers/          thin HTTP layer: read the tenant from ITenantContext, call a service, pick the status code
  Services/             business rules (validation, ingestion, search, verification, exports, retention, summary)
  Repositories/         ALL SQL (Dapper). Every query takes the tenant id and filters on it
  Models/               entities (one class per table row) + enums
  Dtos/                 request/response shapes (what Swagger and the React client see)
  Workers/              BackgroundServices: ExportWorker, RetentionWorker (+ backoff helper)
  Auth/                 API-key handler (producers), header handler (auditors), ITenantContext
  Infrastructure/       ProblemDetails/error handling, correlation id, DB + migrator, metrics, health checks
  Data/DbSeeder.cs      reproducible demo data
  Migrations/001_schema.sql   tables, constraints, indexes, triggers, purge function
audit-ui/src/
  api/                  generated OpenAPI types (schema.d.ts), typed client, TanStack Query hooks, error mapping
  pages/                Activity log, Activity detail, Dashboard, Exports, Tamper check, Retention & holds
  components/           layout, badges, error box, metadata view, dialogs…
  lib/                  URL filters, time + plain-language formatting
```

**Why these choices**
- **Controllers** (not Minimal APIs): familiar structure, attributes give clean OpenAPI metadata.
- **Dapper + raw SQL** (not EF Core): the important parts are SQL features — `SELECT … FOR UPDATE`, `FOR UPDATE SKIP LOCKED`,
  binary `COPY`, row-value keyset pagination, `percentile_cont`. Writing them directly keeps them visible and explainable.
- **Singleton services/repositories**: they hold no state except the thread-safe `NpgsqlDataSource`, so workers can use them
  without creating scopes. Only `ITenantContext` is scoped (it reads the current request's user).
- **React + TypeScript**: typed client generated from the API's OpenAPI document; TanStack Query handles caching,
  cancellation and retries.

---

## 3. How it works (plain language first, then the details)

### 3.1 Ingestion: "exactly once" from "at least once"

A producer may send the same event twice (retries after a timeout). Each event carries the producer's own `eventId`.
The database has a unique key on **(tenant, producer, eventId)**:

| Sent again with… | Result | HTTP (single) |
|---|---|---|
| same eventId, same content | `Duplicate` + the **original** sequence number | 200 |
| same eventId, different content | `Conflict` (never overwritten) | 409 |
| new eventId | `Accepted` + new sequence | 201 |

"Same content" is decided by a SHA-256 **payload fingerprint** stored with the event.

**Batches (max 500)**: every event is validated on its own; valid ones are stored even if others fail. The response lists
a result per event. Status: **200** if everything was accepted/duplicate, **207 Multi-Status** if any event was rejected or
conflicted (the caller must read the per-event results). Empty batch → 400, more than 500 → 413.

**When is it durable?** The API answers only **after the transaction commits**. A 2xx means the events are on disk.
On a timeout the producer doesn't know → it retries with the same eventIds; anything already stored comes back as `Duplicate`.

**Validation** ([EventValidator](AuditTrail.Api/Services/EventValidator.cs)): required fields, enums, dotted action
(`payment.approved`, ≤128 chars), metadata must be a JSON object ≤16 KB, `occurredAt` may be at most **5 minutes in the
future** and not before 2015-01-01. **Redaction**: metadata keys listed for the tenant (`password`, `cardNumber`, `ssn`, …)
are replaced by `[REDACTED]` **before** hashing and storage — the secret never reaches the database or the logs.

### 3.2 The hash chain (tamper evidence)

Every tenant has its own chain. Record *n* stores:

```
hash(n) = SHA-256( hash(n-1) || canonical(record n) )        hash(0) = 32 zero bytes
```

Change, delete or reorder any record and its hash no longer matches — and every later one breaks too.

**Gap-free sequence without races** ([EventRepository.AppendAsync](AuditTrail.Api/Repositories/EventRepository.cs)):

```sql
BEGIN;
SET LOCAL lock_timeout = '5s';
SELECT last_sequence, last_hash FROM chain_heads WHERE tenant_id = @t FOR UPDATE;  -- the per-tenant "chain lock"
SELECT event_id, sequence, payload_hash FROM audit_events                          -- idempotency check
 WHERE tenant_id=@t AND producer_id=@p AND event_id = ANY(@ids);
-- in C#: sequence = head+1, head+2 …; hash = SHA-256(prev || canonical)
COPY audit_events (...) FROM STDIN (FORMAT BINARY);                                -- bulk insert
UPDATE chain_heads SET last_sequence=@seq, last_hash=@hash WHERE tenant_id=@t;
COMMIT;                                                                             -- rows + head become visible together
```

- Works across **any number of API instances**: the lock is a PostgreSQL row lock, not an in-process lock.
- **Crash after taking the lock but before commit?** PostgreSQL rolls back: no rows, head unchanged, lock released. No gap.
  The producer retries and the events are stored once.
- Different tenants never wait for each other. A batch of 500 pays for **one** lock round-trip — batching is how a hot
  tenant gets throughput (measured on a laptop: ~65 ms per 100-event batch; ~2,800 events/s with 16 concurrent batch senders).
- Backed by constraints: `UNIQUE (tenant_id, sequence)` and `UNIQUE (tenant_id, producer_id, event_id)`.

**Canonical serialization** ([CanonicalSerializer](AuditTrail.Api/Services/CanonicalSerializer.cs)) is hand-written so the
hashed bytes never change with a .NET or library upgrade: a JSON array of fields in a fixed order starting with `"v1"`,
UTC timestamps with microseconds, metadata with sorted keys and normalised numbers. A unit test pins a golden hash.
Each row stores `hash_version`; a future format would be `v2` with v1 kept for verifying old rows.

**Append-only in the database**, not just in code: a trigger rejects every `UPDATE` and `TRUNCATE` on `audit_events`,
and every `DELETE` except from the retention function.

**Two clocks, on purpose**: `occurredAt` = when the producer says it happened (can be late or out of order) — search sorts
by it. `receivedAt` / `sequence` = when the platform committed it (strictly increasing) — verification walks this.

### 3.3 Tamper check (verification)

[IntegrityService](AuditTrail.Api/Services/IntegrityService.cs) walks records in sequence order, recomputing each hash.
It starts from the newest **checkpoint** before the requested range (a checkpoint = "chain verified up to record N, which
had hash H"; the retention worker writes one each run). It reports the verified count, the checkpoint used and the
**first broken record** with the reason (`content_modified`, `missing`, `prev_hash_mismatch`).

**Try it yourself** (simulates someone with database-admin rights editing record #100):

```powershell
Get-Content scripts\tamper.sql | docker compose exec -T postgres psql -U audit -d audittrail -v seq=100
```

Then press **Run tamper check** in the console → *"Problem found: record #100 was changed after it was saved"* with a
link to the record. The script also has commented options to delete a record or swap two records.

### 3.4 Retention and legal holds (and how they coexist with the chain)

- Retention days are set **per category** (e.g. Security 7 years, Access 90 days).
- Deletion happens only in the SQL function `purge_expired_events` (in [001_schema.sql](AuditTrail.Api/Migrations/001_schema.sql)),
  in **bounded batches** (500), each its own short transaction.
- **The chain survives deletes**: before a row is deleted, a "skeleton" (sequence + prev hash + hash, no content) is
  copied to `purged_events`. Verification checks the link through skeletons, so the chain still verifies end to end.
- **Legal holds win, even during a running purge**: creating a hold takes `FOR NO KEY UPDATE` on the tenant row; each purge
  batch takes `FOR SHARE` on the same row. These lock modes conflict, so a purge batch waits for an uncommitted hold, and its
  `DELETE … WHERE NOT EXISTS (active hold)` then sees it. (Tested in `Legal_hold_created_while_a_purge_batch_is_open…`.)
- Before purging, the worker verifies the chain; **if it is broken, nothing is deleted** (purging could destroy evidence).
- Auditors get **403** on any change; only ComplianceAdmin can edit.

### 3.5 Search

- `from`/`to` (OccurredAt) are **required, max 90 days** → no query can scan a tenant's whole history (400 otherwise).
- Filters: actor, action prefix, item type/id, result, category, source system. Indexed: `(tenant_id, occurred_at, sequence)`,
  `(tenant_id, actor_id, occurred_at)`, `(tenant_id, resource_type, resource_id, occurred_at)`. The other filters are applied
  while scanning the time-range index — acceptable because the range is bounded. Metadata (jsonb) is **not** indexed.
- **Cursor pagination**: the opaque cursor holds the last row's `(occurredAt, sequence)`, the snapshot head sequence, and a hash
  of the filter, signed with HMAC → tampered cursor = 400. Pages are **stable while new events arrive** because the first
  page pins `sequence <= head`. Oldest-first by default; `newestFirst=true` for newest-first (the console's default).

### 3.6 Exports (streaming, resumable, cancellable)

State machine, enforced by a database trigger:
`Queued → Running → Completed | Failed`, `Queued/Running → Cancelled`, `Completed → Expired`, `Running → Queued` (lease lost).

- `POST /api/exports` needs an **Idempotency-Key** header (a double-click or network retry returns the same job). Returns
  **202 + Location**. Same key with a different body → 409.
- [ExportWorker](AuditTrail.Api/Workers/ExportWorker.cs) claims jobs with `FOR UPDATE SKIP LOCKED` (safe on many instances) and
  a 30 s **lease**. It reads `Export:ChunkSize` rows at a time (500; the Docker demo uses 100 rows + a 1 s pause per chunk so
  you can watch progress, cancel, or kill it mid-file) and writes NDJSON straight to disk (memory = one chunk).
- After each chunk: flush file to disk → save `resume_cursor`, `row_count`, `file_bytes` and renew the lease.
- **Worker killed mid-file?** The lease expires → job goes back to Queued → the next worker truncates the file to `file_bytes`
  and continues after `resume_cursor`: no duplicate or missing rows. After 3 attempts → Failed.
- The export only includes events up to the sequence that existed when it was requested, so a resumed run is identical.
- Download returns the file with an `X-Checksum-SHA256` header; the console re-computes the hash in the browser.
- Cancelling a finished job → **409** with `currentStatus`.

### 3.7 Tenant isolation and roles

- **Producers**: `X-Api-Key` → SHA-256 → `producers` row → tenant. The tenant comes only from the key, so writing into
  another tenant is impossible by construction (and a composite foreign key `(tenant_id, producer_id)` enforces it in the DB).
- **Auditors**: `X-Tenant-Id` + `X-Role` headers become claims (a stand-in for SSO). Controllers read the tenant **only** from
  `ITenantContext`; every repository query has `WHERE tenant_id = @tenantId`. Another tenant's event id → **404, not 403**
  (we don't even confirm it exists). Tested.
- **Real authentication**: producers would use OAuth client credentials or mTLS; the console would use SSO (OIDC) with
  tenant and role as token claims, the token in an HTTP-only cookie via a small BFF (or in memory with silent refresh).
  Only `AuditorHeaderAuthHandler` changes. The console stores nothing secret: only the selected org/role in `sessionStorage`.
- **The platform audits itself**: exports, cancellations, retention changes, holds and purges are written as events into the
  tenant's own chain, in the same transaction as the change.

### 3.8 Errors, logging, health, metrics

- Every error is **RFC 7807 ProblemDetails** with a stable `code` (e.g. `time_range_too_large`), field `errors`, and the
  `correlationId`. No stack traces, SQL or connection strings. Database down → **503** with `Retry-After`.
- Each request gets an `X-Correlation-Id` (shown in the console as a "reference code for support") and log lines carry
  CorrelationId, TenantId, ProducerId, JobId. Metadata and IPs are never logged.
- `/health/live` = process is running (no dependencies, so a DB outage doesn't make the orchestrator restart healthy pods).
  `/health/ready` = PostgreSQL reachable **and** worker heartbeats fresh (take the instance out of the load balancer).
- Metrics (`System.Diagnostics.Metrics`, meter `AuditTrail`): events accepted / duplicates / conflicts / rejected by reason,
  ingest duration, chain lock wait, export rows, retention deleted, verification failures.
  Watch live: `dotnet-counters monitor -n AuditTrail.Api --counters AuditTrail`.

### 3.9 Reliability settings (deliberate values)

| Setting | Value | Why |
|---|---|---|
| Connection pool | 50 per instance | keep instances × 50 below PostgreSQL `max_connections` |
| Connect timeout | 5 s | fail fast with 503 when the DB is down |
| Command timeout | 30 s | no runaway statements |
| Chain `lock_timeout` | 5 s | a hot tenant returns 503 instead of queueing forever; producer retries |
| Export lease | 30 s, renewed per chunk | a dead worker's job is picked up within ~30 s |
| Shutdown timeout | 30 s | in-flight requests finish; the export worker hands its job back (Running → Queued) |
| Worker backoff | 1 s → 60 s, ±20 % jitter | survive DB outages without hammering it |

Everything is async with `CancellationToken` passed from HTTP → service → Dapper. No `.Result` / `.Wait()`.

---

## 4. API overview

Full, interactive documentation: **http://localhost:8080/swagger** (OpenAPI: `/openapi/v1.json`).

| Method & route | Who | Purpose |
|---|---|---|
| `POST /api/ingest/events` | producer | one event (201 / 200 duplicate / 409 conflict / 400) |
| `POST /api/ingest/events/batch` | producer | up to 500 events (200 / 207 / 400 / 413) |
| `GET /api/events?from&to&…&cursor&limit&newestFirst` | auditor | search |
| `GET /api/events/{id}` | auditor | one event incl. sequence + hashes |
| `POST /api/integrity/verify` | auditor | verify `{ fromSequence?, toSequence? }` |
| `POST /api/exports` (+ `Idempotency-Key`) | auditor | create export (202 + Location) |
| `GET /api/exports`, `GET /api/exports/{id}` | auditor | list / status + progress |
| `POST /api/exports/{id}/cancel` | auditor | cancel (409 if finished) |
| `GET /api/exports/{id}/download` | auditor | NDJSON file + `X-Checksum-SHA256` |
| `GET /api/retention/policies` · `PUT /api/retention/policies/{category}` | auditor · admin | retention |
| `GET/POST /api/legal-holds` · `POST /api/legal-holds/{id}/release` | auditor · admin | legal holds |
| `POST /api/retention/run` | admin | purge now (verifies first) |
| `GET /api/summary?from&to` | auditor | counts, top denied actors, lag p50/p95, checkpoint, holds, exports |
| `GET /api/producers` · `GET /api/tenants` (demo only) | auditor · anyone | pickers in the console |

Ready-made requests: [AuditTrail.Api.http](AuditTrail.Api/AuditTrail.Api.http).

---

## 5. The console (frontend)

Screens: **Activity log** (search), **Activity detail**, **Dashboard**, **Exports**, **Tamper check**, **Retention & holds**.
The wording is deliberately non-technical ("Payment approved", "Hidden for privacy", "Kept for 7 years"); the exact
technical values (ids, hashes, raw JSON) are one click away for auditors who need them.

- **Typed API client**: `npm run gen:api` regenerates [schema.d.ts](audit-ui/src/api/schema.d.ts) from the running API's
  OpenAPI document; [openapi-fetch](audit-ui/src/api/client.ts) checks every path, parameter and body at compile time.
  Re-run it whenever a DTO changes (a mismatch then fails `npm run build`).
- **Server state** ([queries.ts](audit-ui/src/api/queries.ts)): TanStack Query. Keys start with tenant + role, so switching
  organization never shows another tenant's cached data. Superseded requests are aborted. Only reads retry, and only on
  network/503 errors.
- **Filters live in the URL** (shareable, survive reload). Changing a filter starts a new query without a cursor.
- **Large lists**: rows are virtualized; infinite scroll + "Load more"; capped at 10,000 rows (export for more).
- **Export button**: one Idempotency-Key per intent — reused for double-clicks and retries, new after success.
- **Errors**: ProblemDetails → plain message, field errors, and the reference code (correlation id). A broken chain is shown
  as a big red *result*, not an error toast. Loading, empty, error and partial ("couldn't load more") states everywhere.
- **Roles**: Auditors see Retention read-only with disabled buttons and an explanation; the API still enforces 403.
- **Security**: metadata is rendered as text (never as HTML — see the seeded `<script>` event, and the test that proves it).
  nginx sends a strict Content-Security-Policy; the API's CORS allows only the console origin.
- **Time**: shown in your time zone ("3 hours ago" + exact time); UTC on hover; only UTC is sent to the API.
- **Accessibility**: keyboard navigation, labelled controls, visible focus ring, native `<dialog>`, sufficient contrast.

---

## 6. At 20,000 events/second, 1,000 tenants, 7-year retention

What breaks first: **the single hot-tenant chain lock.** One tenant producing 60 % of traffic (12,000/s) means one row lock
serialising every transaction for that tenant (~2–3k events/s per chain here). Then: write I/O and index maintenance on one
huge table, then retention `DELETE`s (bloat, vacuum), then 50-million-row exports competing with ingestion.

What I would change:
1. **Write-ahead queue in front of chaining**: accept = durably append to a log (Kafka / a partitioned staging table), return
   202; a single sequencer per tenant (or per chain shard) chains in large batches. Accepted ≠ chained becomes explicit.
2. **Shard the chain** for very hot tenants: N independent chains per tenant (by producer), plus a periodic Merkle root
   across them, instead of one global order.
3. **Time-partitioned tables** (monthly) — retention becomes `DROP PARTITION` (instant, no bloat); keep per-partition
   Merkle roots + signed checkpoints so dropped ranges remain provable.
4. **Cold storage**: older partitions moved to object storage (Parquet) with their Merkle roots; verification checks roots.
5. **Search index** (OpenSearch) fed asynchronously for free-text / metadata search; PostgreSQL stays the source of truth.
   The console's search would then allow longer ranges and full-text, with "results may lag by a few seconds".
6. **Noisy neighbours**: per-tenant and per-producer rate limits, separate worker pools / databases for the largest tenants.
7. **Exports** of 50M rows: run against a read replica, write directly to object storage, multipart, resumable per chunk.

---

## 7. What I simplified / would do next

- Auth is a header stand-in (by design of the brief). Next: OIDC for the console, client credentials for producers.
- Checkpoints are not cryptographically **signed**; next: HMAC/asymmetric signature for offline verification.
- No table partitioning, no row-level security, no rate limiting (listed above as next steps).
- Only NDJSON exports (CSV was optional). No OpenTelemetry tracing exporter (metrics + structured logs only).
- No end-to-end browser test for now (removed on request); the UI is covered by component tests and the API by
  integration tests against real PostgreSQL.

---

## 8. AI tools used

Built with an AI coding assistant (GitHub Copilot in VS Code) for scaffolding, SQL/C#/React drafts and tests.
Every design decision (chain lock, canonical format, purge locking, export leases) was reviewed and verified by running
the tests and the stack: concurrent ingestion gap-free, tampering detected at the exact record, export resumed after a
simulated crash with a matching checksum, hold-vs-purge race. Suggestions that were changed: a non-deterministic
"stable hash" test (replaced by a golden value), oldest-first default list order in the console (changed to newest first),
and nginx security headers that were silently dropped in some locations (fixed with a shared include).
