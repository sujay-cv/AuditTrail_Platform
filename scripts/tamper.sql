-- Tamper with ONE stored event so you can watch integrity verification fail at exactly that sequence.
--
-- Run (Docker):
--   docker compose exec -T postgres psql -U audit -d audittrail -v seq=100 < scripts/tamper.sql
-- Then in the console: Integrity Check -> Verify  (or POST /api/integrity/verify)  ->  "first broken sequence: 100".
--
-- The append-only trigger blocks UPDATE/DELETE. To simulate an attacker with database-admin rights we switch
-- triggers off for this transaction (session_replication_role = replica needs superuser), which is exactly the
-- kind of tampering the hash chain exists to detect.

\set tenant '11111111-1111-1111-1111-111111111111'

BEGIN;
SET LOCAL session_replication_role = replica;

-- Option 1 (default): change the content of one event
UPDATE audit_events
SET    metadata = metadata || '{"tampered": true}'
WHERE  tenant_id = :'tenant' AND sequence = :seq;

-- Option 2: delete one event instead (verification reports reason "missing")
-- DELETE FROM audit_events WHERE tenant_id = :'tenant' AND sequence = :seq;

-- Option 3: swap two neighbouring events (verification fails at the first of the two)
-- UPDATE audit_events SET sequence = -1        WHERE tenant_id = :'tenant' AND sequence = :seq;
-- UPDATE audit_events SET sequence = :seq      WHERE tenant_id = :'tenant' AND sequence = :seq + 1;
-- UPDATE audit_events SET sequence = :seq + 1  WHERE tenant_id = :'tenant' AND sequence = -1;

COMMIT;

SELECT sequence, event_id, metadata FROM audit_events WHERE tenant_id = :'tenant' AND sequence = :seq;
