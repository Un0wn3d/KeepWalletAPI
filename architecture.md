# Architecture

## 1. System Goals

KeepWallet backend is designed to be:
- Secure by default (strong password hashing, token rotation, strict cookie policy).
- Offline-first (SQLite local cache with deterministic sync to PostgreSQL).
- Scalable (modular boundaries, stateless API instances, background workers).
- Maintainable (clean layering, explicit use cases, testable business logic).

## 2. Architectural Style

Recommended style: **Modular Clean Architecture** (DDD-inspired).

- Clean Architecture keeps business rules independent from framework and storage.
- DDD-inspired modules keep complex finance domains isolated and explicit.
- Modular monolith is the initial deployment model; modules can be extracted later if needed.

## 3. Layers and Responsibilities

| Layer | Responsibility | Main Components |
|---|---|---|
| API | HTTP transport, authentication entry, input/output contracts | Controllers, request DTOs, response DTOs, middleware |
| Application | Use cases, orchestration, validation, transaction boundaries | Services, command/query handlers, validators |
| Domain | Business rules and invariants | Entities, value objects, domain services, domain events |
| Infrastructure | External concerns and persistence | EF Core DbContext, repositories, token providers, outbox/sync adapters |

### Dependency Rule

- API depends on Application.
- Application depends on Domain abstractions.
- Infrastructure implements Domain/Application interfaces.
- Domain depends on no outer layer.

## 4. Controller-Service-Repository Pattern

### Controller

- Validates HTTP-level concerns (format, auth context, model binding).
- Calls a single application use case.
- Returns standardized success/error payload.

### Service (Application Use Case)

- Enforces business flow (permission checks, invariants, transaction scope).
- Calls repository interfaces.
- Publishes domain events when state changes.

### Repository

- Persists aggregates and executes query projections.
- No business rules; only data access logic.
- Uses optimistic concurrency (`row_version`) where required.

## 5. Bounded Modules

### Identity and Access

- Registration, login, logout, refresh token rotation.
- Device sessions and token revocation.
- Role/permission checks.

### Transactions

- Income and expense entries.
- Category management.
- Querying history by date, account, category, and amount range.

### Savings

- Savings goal CRUD.
- Sub-goal planning.
- Interest settings and rate history.
- Periodic interest accrual records.

### Recurring Payments

- Daily/weekly/monthly/yearly/custom recurrence rules.
- Next occurrence computation.
- Automatic transaction generation worker.

### Groups

- Group creation and membership.
- Shared expenses.
- Split rules (equal, fixed amount, percentage).
- Settlement tracking.

### Sync

- Outbox ingestion from client.
- Server change feed for pull sync.
- Conflict detection and resolution policies.

## 6. Data Flow

### Online request flow

1. Client sends request with access token cookie or bearer token.
2. API authenticates and authorizes request.
3. Controller calls application service.
4. Service executes domain logic and writes via repository.
5. Repository persists to PostgreSQL.
6. Change log entry is written for sync subscribers.
7. API returns DTO response.

### Offline-to-online flow

1. Client writes to local SQLite and appends local outbox operation.
2. Sync job sends pending operations to `POST /api/v1/sync/push`.
3. Server applies operations idempotently and returns accepted/conflicts.
4. Client requests server delta from cursor with `GET /api/v1/sync/pull`.
5. Client applies remote changes and updates local cursor.

## 7. Background Processing

Use hosted services (or Hangfire/Quartz in larger deployments):

- `RecurringPaymentWorker`: creates due transactions.
- `InterestAccrualWorker`: computes periodic savings interest entries.
- `RefreshTokenCleanupWorker`: deletes expired/revoked tokens.
- `SyncCompactionWorker`: archives old sync log rows by retention policy.

## 8. Cross-Cutting Concerns

- Global exception middleware with standardized error payload.
- Structured logging with correlation IDs.
- Validation pipeline (FluentValidation or DataAnnotations + custom validators).
- Rate limiting on authentication and sync endpoints.
- Audit logging for sensitive operations (login, password changes, group settlements).

## 9. Recommended Project Structure

```text
src/
  KeepWallet.API/
    Controllers/
    Contracts/
    Middleware/
    Program.cs
  KeepWallet.Application/
    Abstractions/
    Auth/
    Transactions/
    Savings/
    Recurring/
    Groups/
    Sync/
  KeepWallet.Domain/
    Common/
    Users/
    Transactions/
    Savings/
    Recurring/
    Groups/
  KeepWallet.Infrastructure/
    Persistence/
    Security/
    Repositories/
    BackgroundJobs/
```

## 10. Scalability Notes

- Keep API stateless; session state must live in DB/cache.
- Use connection pooling and `AsNoTracking` for read-heavy endpoints.
- Add Redis cache for category lists and read models if needed.
- Partition or archive large tables (`transactions`, `sync_change_log`) as data grows.
- Move background workers to separate process when workload increases.
