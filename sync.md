# Offline Sync Design

## 1. Offline-First Principles

- The client writes to SQLite first for fast UX.
- PostgreSQL remains the global source of truth.
- Sync is eventually consistent and deterministic.
- Every write operation is idempotent.

## 2. Local Write Model (SQLite)

Each user action executes in one SQLite transaction:

1. Apply change to local business table.
2. Append operation to `local_outbox`.
3. Update local entity `row_version` placeholder if needed.

`local_outbox` fields:
- `operation_id` (uuid)
- `entity_name`
- `entity_id`
- `action` (`create`, `update`, `delete`)
- `base_row_version`
- `payload_json`
- `created_at`
- `retry_count`
- `last_error`

## 3. Sync Direction and Order

Always execute in this order:

1. **Push** local pending operations.
2. **Pull** server changes since last cursor.
3. **Acknowledge** and persist new cursor.

This avoids overwriting local pending writes with stale server snapshots.

## 4. Server Change Tracking

Server tracks all committed changes in `sync_change_log`.

Each row contains:
- `sequence_id` (monotonic global sequence)
- `owner_user_id`
- `entity_name`, `entity_id`
- `operation`
- `row_version`
- `changed_at`
- optional compact `payload`

Changes are inserted:
- In the same DB transaction as business write.
- After each create/update/delete through repository/unit-of-work hooks.

## 5. Push API Contract

`POST /api/v1/sync/push`

For every operation:

1. Check `operation_id` in `processed_sync_operations`.
2. If already processed, return previous result (idempotent replay).
3. Validate permission and ownership.
4. Compare `base_row_version` with current server row version.
5. Apply or reject with conflict details.
6. Log processed operation and emit change log row if applied.

## 6. Pull API Contract

`GET /api/v1/sync/pull?afterSequence={n}&limit={m}`

Server returns:
- All rows from `sync_change_log` with `sequence_id > afterSequence` for current user scope.
- Ordered ascending by `sequence_id`.
- `nextSequence` for cursor advancement.

Client applies changes in order and sets `local_sync_state.last_server_sequence_id = nextSequence`.

## 7. Conflict Detection

Conflict occurs when client writes based on stale state.

Condition:
- Client `base_row_version != server row_version`

Also conflict when:
- Entity was deleted on server but client attempts update.
- Ownership changed (forbidden update across tenant boundary).

## 8. Conflict Resolution Policy

Use policy per domain because financial data has different sensitivity.

### Strict server-wins (reject client write)

Use for:
- `transactions`
- `group_expenses`
- `group_settlements`

Reason:
- Financial records must remain auditable.

Client behavior:
- Store in `local_conflicts`.
- Show user explicit resolution UI.

### Field-level merge (safe merge)

Use for:
- `savings_goals` descriptive fields (`description`, optional notes)
- `categories` metadata where non-overlapping fields changed

Condition:
- Changed fields do not overlap with server-modified fields.

### Last-write-wins with audit trail

Use for:
- Non-critical preferences only (for example, UI settings).

Never use LWW for money amounts or settlements.

## 9. Deletes and Tombstones

Use soft deletes for syncable rows:
- `is_deleted = true`
- `deleted_at = now()`
- Increment `row_version`

Why:
- Offline devices need delete events.
- Prevents resurrecting deleted rows during delayed sync.

Hard delete only by retention jobs after all active devices pass retention window.

## 10. Retry and Backoff

- Retry push with exponential backoff and jitter.
- Keep operation order stable per entity stream.
- Pause retries on `401` and refresh tokens first.
- Mark permanently failed operations after configurable threshold and surface in UI.

## 11. Bootstrap and Re-Sync

### First login on a device

1. Authenticate.
2. Pull full snapshot in pages.
3. Store server cursor.

### Corrupted local state recovery

1. Stop sync.
2. Keep unsent local outbox backup.
3. Re-bootstrap from server.
4. Replay recoverable local operations with conflict checks.

## 12. Security in Sync

- Sync endpoints require authenticated user and bound `X-Device-Id`.
- Operation payloads must be schema-validated server-side.
- Reject operations that reference another user's resources.
- Log suspicious replay or out-of-order patterns.

## 13. Observability and Metrics

Track:
- Push success/failure rate
- Conflict rate per entity
- Average sync latency
- Average outbox size per device
- Time-to-consistency across devices

These metrics are required to maintain a healthy offline-first system in production.
