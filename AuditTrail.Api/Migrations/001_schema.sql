-- 001_schema.sql
-- Everything that protects integrity is enforced here, in the database, not only in C#.

-- ---------------------------------------------------------------------------
-- Tenants, producers, chain heads
-- ---------------------------------------------------------------------------
CREATE TABLE tenants (
    id              uuid        PRIMARY KEY,
    name            text        NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    -- Metadata keys (case-insensitive, any depth) whose values are masked before storage and hashing.
    redaction_keys  text[]      NOT NULL DEFAULT '{}'
);

CREATE TABLE producers (
    id            uuid        PRIMARY KEY,
    tenant_id     uuid        NOT NULL REFERENCES tenants (id),
    name          text        NOT NULL,
    -- SHA-256 (hex) of the API key. The key itself is never stored.
    api_key_hash  text        NOT NULL UNIQUE,
    status        text        NOT NULL CHECK (status IN ('Active', 'Revoked')),
    created_at    timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, name),
    -- Lets audit_events reference (tenant_id, producer_id): an event can only point at a producer of the SAME tenant.
    UNIQUE (tenant_id, id)
);

-- One row per tenant. Locking this row (SELECT ... FOR UPDATE) is "the chain lock" for that tenant.
CREATE TABLE chain_heads (
    tenant_id         uuid        PRIMARY KEY REFERENCES tenants (id),
    last_sequence     bigint      NOT NULL DEFAULT 0,
    last_hash         bytea       NOT NULL DEFAULT decode(repeat('00', 32), 'hex'),
    last_received_at  timestamptz NOT NULL DEFAULT 'epoch'
);

-- ---------------------------------------------------------------------------
-- The append-only event store
-- ---------------------------------------------------------------------------
CREATE TABLE audit_events (
    id                uuid        PRIMARY KEY,
    tenant_id         uuid        NOT NULL REFERENCES tenants (id),
    producer_id       uuid        NOT NULL,
    event_id          text        NOT NULL,           -- producer supplied; the idempotency key
    sequence          bigint      NOT NULL,           -- per tenant, gap free, assigned under the chain lock
    occurred_at       timestamptz NOT NULL,           -- producer clock
    received_at       timestamptz NOT NULL,           -- server clock, strictly increasing per tenant
    actor_type        text        NOT NULL CHECK (actor_type IN ('User', 'Service', 'System')),
    actor_id          text        NOT NULL,
    actor_ip          text,
    actor_user_agent  text,
    action            text        NOT NULL CHECK (char_length(action) <= 128),
    resource_type     text        NOT NULL,
    resource_id       text        NOT NULL,
    outcome           text        NOT NULL CHECK (outcome IN ('Success', 'Failure', 'Denied')),
    category          text        NOT NULL CHECK (category IN ('Security', 'Access', 'DataChange', 'Configuration', 'Financial')),
    metadata          jsonb       NOT NULL DEFAULT '{}',
    payload_hash      bytea       NOT NULL,           -- fingerprint of the producer payload: same EventId + different payload = conflict
    hash_version      smallint    NOT NULL DEFAULT 1, -- which canonical serialization produced `hash`
    prev_hash         bytea       NOT NULL,
    hash              bytea       NOT NULL,
    CONSTRAINT fk_events_producer FOREIGN KEY (tenant_id, producer_id) REFERENCES producers (tenant_id, id),
    CONSTRAINT uq_events_idempotency UNIQUE (tenant_id, producer_id, event_id),
    CONSTRAINT uq_events_sequence UNIQUE (tenant_id, sequence)
);

-- Search: tenant + OccurredAt range, keyset ordered by (occurred_at, sequence).
CREATE INDEX ix_events_time     ON audit_events (tenant_id, occurred_at, sequence);
-- Selective filters that deserve their own index.
CREATE INDEX ix_events_actor    ON audit_events (tenant_id, actor_id, occurred_at);
CREATE INDEX ix_events_resource ON audit_events (tenant_id, resource_type, resource_id, occurred_at);

-- When retention deletes an event it keeps this tiny "skeleton" (sequence + hashes, no content),
-- so the hash chain can still be walked end to end after deletes.
CREATE TABLE purged_events (
    tenant_id  uuid        NOT NULL REFERENCES tenants (id),
    sequence   bigint      NOT NULL,
    prev_hash  bytea       NOT NULL,
    hash       bytea       NOT NULL,
    purged_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, sequence)
);

-- "The chain from sequence 1 up to `sequence` was verified at created_at and ended with `hash`."
CREATE TABLE integrity_checkpoints (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id   uuid        NOT NULL REFERENCES tenants (id),
    sequence    bigint      NOT NULL,
    hash        bytea       NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, sequence)
);

-- ---------------------------------------------------------------------------
-- Retention + legal holds
-- ---------------------------------------------------------------------------
CREATE TABLE retention_policies (
    tenant_id       uuid        NOT NULL REFERENCES tenants (id),
    category        text        NOT NULL CHECK (category IN ('Security', 'Access', 'DataChange', 'Configuration', 'Financial')),
    retention_days  integer     NOT NULL CHECK (retention_days BETWEEN 1 AND 36500),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, category)
);

CREATE TABLE legal_holds (
    id           uuid        PRIMARY KEY,
    tenant_id    uuid        NOT NULL REFERENCES tenants (id),
    scope_type   text        NOT NULL CHECK (scope_type IN ('Actor', 'Resource', 'Category')),
    -- Actor: actor id.  Resource: 'Type/Id'.  Category: category name.
    scope_value  text        NOT NULL,
    reason       text        NOT NULL CHECK (char_length(btrim(reason)) > 0),
    created_by   text        NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    released_at  timestamptz,
    released_by  text
);
CREATE INDEX ix_legal_holds_active ON legal_holds (tenant_id) WHERE released_at IS NULL;

-- ---------------------------------------------------------------------------
-- Export jobs
-- ---------------------------------------------------------------------------
CREATE TABLE export_jobs (
    id                  uuid        PRIMARY KEY,
    tenant_id           uuid        NOT NULL REFERENCES tenants (id),
    filter              jsonb       NOT NULL,
    format              text        NOT NULL CHECK (format IN ('Ndjson')),
    status              text        NOT NULL CHECK (status IN ('Queued', 'Running', 'Completed', 'Failed', 'Cancelled', 'Expired')),
    requested_by        text        NOT NULL,
    idempotency_key     text        NOT NULL,
    request_hash        text        NOT NULL,         -- SHA-256 of the request body; same key + different body = 409
    snapshot_sequence   bigint      NOT NULL,         -- export only includes events up to this sequence, so a resumed run is identical
    lease_owner         text,
    lease_expires_at    timestamptz,
    attempts            integer     NOT NULL DEFAULT 0,
    resume_cursor       jsonb,                        -- {"occurredAt": ..., "sequence": ...} of the last row written
    row_count           bigint      NOT NULL DEFAULT 0,
    file_bytes          bigint      NOT NULL DEFAULT 0, -- length of the partial file that resume_cursor covers
    file_location       text,
    checksum            text,                         -- SHA-256 hex of the finished file
    error               text,
    created_at          timestamptz NOT NULL DEFAULT now(),
    completed_at        timestamptz,
    UNIQUE (tenant_id, idempotency_key)
);
CREATE INDEX ix_export_jobs_lease  ON export_jobs (status, lease_expires_at);
CREATE INDEX ix_export_jobs_tenant ON export_jobs (tenant_id, created_at DESC);

-- ---------------------------------------------------------------------------
-- Append-only enforcement
-- ---------------------------------------------------------------------------
CREATE FUNCTION audit_events_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' THEN
        RAISE EXCEPTION 'audit_events is append-only: UPDATE is not allowed' USING ERRCODE = '42501';
    END IF;
    -- DELETE is only allowed from purge_expired_events(), which turns this flag on for its own transaction only.
    IF coalesce(current_setting('audit.purge', true), 'off') <> 'on' THEN
        RAISE EXCEPTION 'audit_events is append-only: DELETE is only allowed through the retention path' USING ERRCODE = '42501';
    END IF;
    RETURN OLD;
END $$;

CREATE TRIGGER trg_audit_events_guard
    BEFORE UPDATE OR DELETE ON audit_events
    FOR EACH ROW EXECUTE FUNCTION audit_events_guard();

CREATE FUNCTION forbid_truncate() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% is append-only: TRUNCATE is not allowed', TG_TABLE_NAME USING ERRCODE = '42501';
END $$;

CREATE TRIGGER trg_audit_events_no_truncate
    BEFORE TRUNCATE ON audit_events
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_truncate();

CREATE FUNCTION forbid_modification() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% is append-only: % is not allowed', TG_TABLE_NAME, TG_OP USING ERRCODE = '42501';
END $$;

CREATE TRIGGER trg_purged_events_guard
    BEFORE UPDATE OR DELETE ON purged_events
    FOR EACH ROW EXECUTE FUNCTION forbid_modification();

-- ---------------------------------------------------------------------------
-- Export job state machine, enforced in the database:
--   Queued    -> Running | Cancelled
--   Running   -> Queued (lease expired / shutdown) | Completed | Failed | Cancelled
--   Completed -> Expired
--   Failed, Cancelled, Expired are terminal and never change.
-- ---------------------------------------------------------------------------
CREATE FUNCTION export_jobs_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status IN ('Failed', 'Cancelled', 'Expired') THEN
        RAISE EXCEPTION 'export job % is % (terminal) and cannot change', OLD.id, OLD.status;
    END IF;
    IF OLD.status = 'Completed' AND NEW.status <> 'Expired' THEN
        RAISE EXCEPTION 'export job % is Completed and can only move to Expired', OLD.id;
    END IF;
    IF NEW.status <> OLD.status AND NOT (
           (OLD.status = 'Queued'    AND NEW.status IN ('Running', 'Cancelled'))
        OR (OLD.status = 'Running'   AND NEW.status IN ('Queued', 'Completed', 'Failed', 'Cancelled'))
        OR (OLD.status = 'Completed' AND NEW.status = 'Expired')) THEN
        RAISE EXCEPTION 'illegal export job transition % -> %', OLD.status, NEW.status;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_export_jobs_guard
    BEFORE UPDATE ON export_jobs
    FOR EACH ROW EXECUTE FUNCTION export_jobs_guard();

-- ---------------------------------------------------------------------------
-- Retention: delete ONE bounded batch, atomically with the legal-hold check. Returns the number purged.
-- ---------------------------------------------------------------------------
CREATE FUNCTION purge_expired_events(p_tenant uuid, p_batch integer) RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    v_deleted integer;
BEGIN
    -- Creating a legal hold takes FOR NO KEY UPDATE on the same tenant row. FOR SHARE and FOR NO KEY UPDATE conflict, so:
    --  * a hold that is being created (not yet committed) makes this purge wait until it commits, and
    --  * a purge batch in flight makes hold creation wait until the batch commits.
    -- The DELETE below starts after this lock is granted, so its snapshot always sees every committed hold.
    -- (Ingestion only takes FOR KEY SHARE on the tenant row via foreign keys, which conflicts with neither.)
    PERFORM 1 FROM tenants WHERE id = p_tenant FOR SHARE;

    PERFORM set_config('audit.purge', 'on', true);   -- 'true' = transaction local

    WITH victims AS (
        SELECT e.id
        FROM audit_events e
        JOIN retention_policies p ON p.tenant_id = e.tenant_id AND p.category = e.category
        WHERE e.tenant_id = p_tenant
          AND e.occurred_at < now() - make_interval(days => p.retention_days)
          AND NOT EXISTS (
                SELECT 1 FROM legal_holds h
                WHERE h.tenant_id = e.tenant_id
                  AND h.released_at IS NULL
                  AND (   (h.scope_type = 'Actor'    AND h.scope_value = e.actor_id)
                       OR (h.scope_type = 'Category' AND h.scope_value = e.category)
                       OR (h.scope_type = 'Resource' AND h.scope_value = e.resource_type || '/' || e.resource_id)))
        ORDER BY e.occurred_at
        LIMIT p_batch
        FOR UPDATE OF e SKIP LOCKED
    ),
    deleted AS (
        DELETE FROM audit_events e USING victims v WHERE e.id = v.id
        RETURNING e.tenant_id, e.sequence, e.prev_hash, e.hash
    ),
    skeleton AS (
        INSERT INTO purged_events (tenant_id, sequence, prev_hash, hash)
        SELECT tenant_id, sequence, prev_hash, hash FROM deleted
        RETURNING 1
    )
    SELECT count(*) INTO v_deleted FROM skeleton;

    PERFORM set_config('audit.purge', 'off', true);
    RETURN v_deleted;
END $$;
