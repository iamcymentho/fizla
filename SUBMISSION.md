# Fizla Submission Notes

Companion to README.md. The README covers how to run it; this file covers why it looks the way it does.

## Explanation

The service exposes `POST /webhooks/transactions` (ASP.NET Core 8). It ingests transaction events from an external provider, derives a structured projection, and persists it in PostgreSQL.

The layout is a thin Clean Architecture split: layers, not patterns. The testable seams pay for themselves without dragging in MediatR, Result types, or repository ceremony.

* **Domain.** `Transaction` is a sealed aggregate with a private constructor and a `Create` factory. The factory derives `Fee = round(Amount * 1.5%, 2, AwayFromZero)` and `NetAmount = Amount - Fee` at construction, so an invalid `Transaction` cannot exist in memory.
* **Application.** One CQRS-lite command (`IngestTransactionCommand`), one FluentValidation validator, one handler. No mediator; a single use case does not justify the dependency.
* **Infrastructure.** EF Core 8 plus Npgsql. Snake-case table `transactions`, `numeric(18,2)` for money, unique index on `external_id`. Schema ships as a checked-in migration.
* **Api.** One controller and one global `ExceptionHandlingMiddleware` that maps `ValidationException` to a `400 ProblemDetails`, keeping the handler free of HTTP concerns.

Idempotency. The handler reads by `ExternalId`. If the row exists it returns the originally persisted projection, otherwise it inserts. The unique index is the hard guard. A concurrent insert that loses the race surfaces as `DbUpdateException`, the handler re-reads the winning row, and both callers see the same response.

Derived computation. `Fee` and `NetAmount` are computed once at ingestion and persisted, not recomputed on read. This freezes the value at the time of ingestion (auditable even if the fee rate later changes) and keeps reads cheap.

Tests (`dotnet test`, 26 pass): domain invariants, fee rounding, UUID v7 verification, handler-level race recovery using a synthetic `DbUpdateException` plus a negative control, and webhook integration tests covering replay, divergent-payload warning, and validator rejections.

## Assumptions (3)

1. **The provider's `id` is the natural idempotency key.** Replays carry the same business payload and should return the original result rather than 409 Conflict. Same id with a different payload is treated as a replay (see Edge Cases below for the trade-off).
2. **Money is in major units, two decimal places.** `numeric(18,2)` end to end, `decimal` everywhere, never `double`. Currency conversion is out of scope.
3. **The webhook is trusted infrastructure.** Network-level auth or HMAC signature verification is handled upstream, so request-body validation is the only trust boundary inside this service.

## Decision Justification (2)

### 1. Idempotency = unique DB index + read-before-insert + race catch

Not a separate `Idempotency-Key` header table, not Redis, not a distributed lock.

The provider already ships a stable business id, so a second idempotency key would duplicate that constraint and let the two diverge. Making the database the source of truth via a unique index means:

* No extra dependency. No Redis, no advisory-lock library.
* Concurrent duplicate deliveries collapse deterministically. The loser's `DbUpdateException` is narrowed to PostgreSQL's unique-violation (`SqlState 23505`) via an `IUniqueViolationDetector` abstraction, then re-reads the winner. Any other DB failure rethrows, so the catch can't accidentally swallow real errors.
* Idempotency survives an app crash mid-request: nothing is committed unless the row landed.

When a replay carries a different payload under the same id, the handler returns the original row and emits a `Warning` log that names both payloads side by side. Stricter approaches (hash and reject with 409) are deferred. The warning is enough for operators to spot provider misbehaviour without silently corrupting the projection.

### 2. Derived fields persisted, not computed on read

Computing `Fee` and `NetAmount` once at ingestion and storing them keeps reads a flat `SELECT`, no view or projection layer. It also makes the projection auditable: if finance later changes the fee rate, the historical value at the time of ingestion is preserved; a recomputed view would silently shift. And it matches how the brief framed structured derived records: a stored output, not a virtual one.

The fee rate is therefore a `const` on the domain entity, not configuration. Configuration would imply the value can change, but the stored field is a historical record and must be reproducible from inputs at the time it was written.

## Rejected Alternative (1)

Outbox pattern with an asynchronous derivation worker.

The shape: persist the raw event in one transaction (alongside an `outbox` row), return `202 Accepted`, and have a background worker compute derived fields and write to a separate projection table, fanning out events to downstream consumers (analytics, ledger, notifications) at the same time.

This is the right shape if derivations are expensive, if there are multiple derivations, or if other services need the event with exactly-once semantics across the boundary.

I rejected it for this brief. A single multiplicative fee does not justify an extra table, a worker process, eventual consistency the API caller has to reason about, and operational overhead (worker liveness, dead letters, dashboard). Synchronous derivation in the handler keeps the public contract simple, the system debuggable, and the code surface around 80 lines instead of 400. If requirements grow (multiple derivations, downstream consumers, expensive computations), promoting to an outbox is a contained refactor: the handler becomes a writer, the derivation moves to a worker, and the public contract is unchanged.

## Failure Scenario (1)

PostgreSQL becomes unavailable mid-request.

`SaveChangesAsync` throws an Npgsql exception that is not a unique-constraint violation, so the catch filter does not match and the exception propagates. `ExceptionHandlingMiddleware` returns `500 ProblemDetails` to the provider.

What is preserved: because no row was committed, the provider's retry arrives at a clean state and either inserts successfully or hits the existing-row branch. Idempotency holds across the outage. No partial writes, no double charges.

What is at risk: the provider's retry policy is unknown. If it gives up after N attempts, an event can be lost.

Production mitigation, not in this build:

* Wrap `SaveChangesAsync` in a Polly transient-fault retry policy with jittered exponential backoff for connection-level failures (Npgsql `IsTransient`).
* Surface a dead-letter queue for events that exceed the retry budget, so they can be replayed once the database recovers.
* Pair with a `/healthz` readiness check so a load balancer pulls the instance before the provider sees a 500.

These were left out because the brief asked for minimal and no over-engineering. They belong in the production-hardening checklist below, not in a screening exercise.

## Edge cases worth naming

| Case | Behaviour | Trade-off |
|---|---|---|
| Same `id`, different payload | Returns original row, ignores new payload, emits a `Warning` log naming both payloads. | First write wins. The warning is enough for operators to spot it. A stricter posture would hash the canonical payload, store it, and reject 409 on mismatch. Out of scope here. |
| Currency lower-case or wrong length | Rejected by validator (`^[A-Z]{3}$`). `Transaction.Create` re-validates at the domain boundary as defence in depth. | Validator is the contract. The entity is the safety net for any future caller that bypasses the use case. |
| Status mis-cased or unknown | Rejected by validator (case-sensitive) and by `Transaction.Create` (single source of truth via `Transaction.AllowedStatuses`). | Eliminates the silent normalisation that case-insensitive matching would have introduced. |
| Amount with more than two decimal places | Rejected at validator and entity. | Money math must not depend on `numeric(18,2)` truncation. |
| `OccurredAt = default` | Rejected at validator and entity. | A real timestamp is required for any temporal reporting. |
| Future-dated `Timestamp` | Accepted. | Provider clocks are trusted; no clock-skew check. |
| Concurrent inserts of same `id` | Unique-violation `SqlState 23505` recognised by `IUniqueViolationDetector`; the loser re-reads the winner. | Both callers receive identical responses. Non-unique DB failures rethrow. |
| `Database.Migrate()` on startup | Runs only in `Development`. | In production, migrations would be a CI step or init container, never on app startup with multiple replicas. |
| Primary key | UUID v7 (sortable, big-endian timestamp prefix). | Random v4 GUIDs cause B-tree page splits on hot write paths. v7 keeps inserts roughly monotonic without giving up global uniqueness. |

## Production-hardening checklist (deferred)

Real production needs that were left out because the brief asked for minimal and no over-engineering. Each is a contained add-on, not a refactor.

| Concern | Deferred mitigation | Why deferred |
|---|---|---|
| Webhook authenticity | HMAC `X-Signature` middleware (per-provider secret) or mTLS | Brief lists no provider auth contract; covered as Assumption 3. |
| Transient DB faults | Polly retry policy on `SaveChangesAsync` (Npgsql `IsTransient` predicate) | Single-attempt is acceptable when the provider already retries at-least-once. |
| Latency on the happy path | Single-trip `INSERT ... ON CONFLICT DO NOTHING RETURNING` via `ExecuteSqlInterpolated` | SELECT-then-INSERT is two round-trips. The saving is real but not material at the throughput this brief implies. |
| Observability | `System.Diagnostics.Metrics` counters for `ingested_total`, `duplicate_total`, `race_total`, `failed_total{reason}`. OpenTelemetry tracing. | Logs cover the screening. Without metrics you cannot tell from production whether idempotency is working. |
| Health and readiness | `app.MapHealthChecks("/healthz")` with `AddDbContextCheck<AppDbContext>()` | Deployment-time concern; no orchestrator in scope. |
| HTTPS and transport | `app.UseHttpsRedirection()` and HSTS | Deployment-time concern; local dev runs on HTTP. |
| Rate limiting | `AddRateLimiter` per-source-IP fixed window | Provider behaviour is contractual. Not the right control point for unauthenticated dev. |
| Body size limit | Lower from default 30 MB to around 64 KB | A small JSON contract; the default is unsafe but not exploited under the assumed trust model. |
| Configuration secrets | User Secrets locally; env or secret manager in deployed envs | `appsettings.json` ships with dev creds only. Never deploy this file. |
| Outbox / fan-out | Transactional outbox plus worker for downstream consumers | Named in Rejected Alternative; adopt when a second consumer appears. |
| Test realism | Swap SQLite in-memory for `Testcontainers.PostgreSql` | Would verify Npgsql-specific behaviour (real `SqlState 23505`, real B-tree). The handler-level race test already covers the catch-filter logic with a synthetic exception. Testcontainers would add provider fidelity. Cost roughly +5s per CI run. |
| Test project naming | Rename `Fizla.UnitTests` to `Fizla.Tests` (it contains both) | Cosmetic. Flagged in this document instead. |

## Run it

```bash
docker compose up -d --build               # API on :5080, PG on :5440
dotnet test                                # 26/26 pass
```

`sample-request.http` covers the happy path, an idempotent replay, and an invalid-payload case for the VS Code REST Client or JetBrains HTTP Client.
