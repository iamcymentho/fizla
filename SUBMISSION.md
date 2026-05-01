# Fizla — Submission Notes

> Companion to `README.md`. The README covers *how to run it*; this file covers
> *why it looks the way it does*.

---

## Explanation

The service exposes `POST /webhooks/transactions` (ASP.NET Core 8). It ingests
transaction events from an external provider, derives a structured projection,
and persists it in PostgreSQL.

The solution uses a thin Clean Architecture split — *layers, not patterns* —
so the testable seams pay for themselves without dragging in MediatR, Result
types, or repository ceremony:

- **Domain.** `Transaction` is a sealed aggregate with a private constructor
  and a `Create` factory. The factory derives
  `Fee = round(Amount × 1.5%, 2, AwayFromZero)` and
  `NetAmount = Amount − Fee` at construction time, so an invalid `Transaction`
  cannot exist in memory.
- **Application.** One CQRS-lite command (`IngestTransactionCommand`),
  one FluentValidation validator, one handler. No mediator — a single use
  case does not justify the dependency.
- **Infrastructure.** EF Core 8 + Npgsql. Snake-case table `transactions`
  with `numeric(18,2)` for money and a unique index on `external_id`. Schema
  ships as a checked-in migration.
- **Api.** One controller; one global `ExceptionHandlingMiddleware` that
  maps `ValidationException` → `400 ProblemDetails` so handler code stays
  free of HTTP concerns.

**Idempotency.** The handler reads by `ExternalId`; if found, it returns the
originally persisted projection. Otherwise it inserts. The unique index is
the hard guard: a concurrent insert that loses the race surfaces as
`DbUpdateException`, the handler re-reads the winning row and returns it.
Two simultaneous deliveries therefore produce one row and identical
responses.

**Derived computation.** `Fee` and `NetAmount` are computed once and
**persisted**, not recomputed on read. This freezes the value at the time of
ingestion (auditable even if the fee rate later changes) and keeps reads
cheap.

**Tests** (`dotnet test`, 26 pass): domain invariants, fee rounding, UUID-v7
verification, handler-level race recovery (synthetic `DbUpdateException`
with negative control), and webhook integration tests covering replay,
divergent-payload warning, and validator rejections.

---

## Assumptions (3)

1. **The provider's `id` is the natural idempotency key.** Replays carry the
   same business payload and should return the original result rather than
   409 Conflict. Divergent-payload-same-id is treated as a replay (see
   *Edge Cases* below for the conscious trade-off).
2. **Money is in major units, two decimal places.** `numeric(18,2)` end to
   end, `decimal` everywhere — never `double`. Currency conversion is out of
   scope.
3. **The webhook is trusted infrastructure** (network-level auth or HMAC
   signature verification handled upstream). Request-body validation is the
   only trust boundary inside this service.

---

## Decision Justification (2)

### 1. Idempotency = unique DB index + read-before-insert + race catch

**Not** a separate `Idempotency-Key` header table, **not** Redis, **not** a
distributed lock.

**Why.** The provider already ships a stable business id, so a second
idempotency key would duplicate that constraint and let the two diverge.
Making the database the source of truth via a unique index means:

- No extra dependency (no Redis, no advisory-lock library).
- Concurrent duplicate deliveries collapse deterministically — the loser's
  `DbUpdateException` is narrowed to PostgreSQL's unique-violation
  (`SqlState 23505`) via an `IUniqueViolationDetector` abstraction, then
  re-reads the winner. Any other DB failure rethrows cleanly, so the catch
  block can't accidentally swallow real errors.
- Idempotency survives an app crash mid-request: nothing is committed
  unless the row landed.

A consequence worth naming: when a replay carries a *different* payload
under the same id, the handler returns the original row and emits a
`Warning` log that names both payloads side by side. Stricter postures
(hash-and-reject with 409) are deferred — the warning is the minimum
viable signal that lets operators detect provider misbehaviour without
silently corrupting the projection.

### 2. Derived fields persisted, not computed on read

**Why.** Computing `Fee` / `NetAmount` once at ingestion and storing them:

- Keeps reads a flat `SELECT` — no view, no projection layer.
- Makes the projection auditable. If finance later changes the fee rate,
  the historical value at the time of ingestion is preserved (a recomputed
  view would silently shift).
- Matches how the brief framed *"structured derived records"* — a stored
  output, not a virtual one.

The fee rate is therefore a `const` on the domain entity, not configuration:
configuration would imply the value can change, but the stored field is a
historical record and must be reproducible from inputs at the time it was
written.

---

## Rejected Alternative (1)

**Outbox pattern + asynchronous derivation worker.**

The shape: persist the raw event in one transaction (alongside an `outbox`
row), return `202 Accepted`, and have a background worker compute derived
fields and write to a separate projection table — fanning out events to
downstream consumers (analytics, ledger, notifications) at the same time.

**Why I considered it.** It's the right shape if (a) derivations are
expensive, (b) there are multiple derivations, or (c) other services need
the event with exactly-once semantics across the boundary.

**Why I rejected it for this brief.** A single multiplicative fee does not
justify the cost: an extra table, a worker process, eventual consistency
that the API caller has to reason about, and operational overhead (worker
liveness, dead letters, dashboard). Synchronous derivation in the handler
keeps the public contract simple, the system debuggable, and the code
surface ~80 lines instead of ~400. If the requirements grow — multiple
derivations, downstream consumers, expensive computations — promoting to
an outbox is a contained refactor: the handler becomes a writer, the
derivation moves to a worker, the public contract is unchanged.

---

## Failure Scenario (1)

**PostgreSQL becomes unavailable mid-request.**

`SaveChangesAsync` throws an `Npgsql` exception that is *not* a
unique-constraint violation, so the catch block's re-read also fails (or
returns null). The handler rethrows; `ExceptionHandlingMiddleware` returns
`500 ProblemDetails` to the provider.

**What is preserved.** Because no row was committed, the provider's retry
arrives at a clean state and either inserts successfully or hits the
existing-row branch — idempotency holds across the outage. No partial
writes, no double charges.

**What is at risk.** The provider's retry policy is unknown. If it gives
up after N attempts, an event can be lost.

**Production mitigation (not in this build, deliberately).**
- Wrap `SaveChangesAsync` in a Polly transient-fault retry policy with
  jittered exponential backoff for connection-level failures (Npgsql
  `IsTransient`).
- Surface a dead-letter for events that exceed the retry budget so they
  can be replayed once the database recovers.
- Pair with a `/healthz` readiness check so a load balancer pulls the
  instance before the provider sees a 500.

These were left out because the brief asked for *minimal* and *no
over-engineering* — they belong in the production hardening checklist, not
in a screening exercise.

---

## Edge cases worth naming

| Case | Behaviour | Conscious trade-off |
|---|---|---|
| Same `id`, different payload | Returns original row, ignores new payload, **emits a `Warning` log naming both payloads** so operators can detect provider misbehaviour. | First write wins. The warning is the minimum viable signal; a stricter posture would hash the canonical payload, store it, and reject 409 on mismatch — out of scope here. |
| Currency lower-case / wrong length | Rejected by validator (`^[A-Z]{3}$`); `Transaction.Create` re-validates at the domain boundary as defence-in-depth. | Validator is the contract; the entity is the safety net for any future caller that bypasses the use case. |
| Status mis-cased or unknown | Rejected by validator (case-sensitive) and by `Transaction.Create` (single source of truth via `Transaction.AllowedStatuses`). | Eliminates the silent normalisation that case-insensitive matching would have introduced. |
| Amount with > 2 decimal places | Rejected at validator and entity. | Money math must not depend on `numeric(18,2)` truncation. |
| `OccurredAt = default` | Rejected at validator and entity. | A real timestamp is required for any temporal reporting. |
| Future-dated `Timestamp` | Accepted | Provider clocks are trusted; no clock-skew check. |
| Concurrent inserts of same `id` | Unique-violation `SqlState 23505` recognised by `IUniqueViolationDetector`; loser re-reads winner. | Both callers receive identical responses; non-unique DB failures rethrow cleanly. |
| `Database.Migrate()` on startup | Runs only in `Development` | In production, migrations would be a CI step or init container — never on app startup with multiple replicas. |
| Primary key | UUID v7 (sortable, big-endian timestamp prefix) | Random v4 GUIDs cause B-tree page splits on hot write paths. v7 keeps inserts roughly monotonic without giving up global uniqueness. |

---

## Production-hardening checklist (explicitly deferred)

These are real production needs but were left out because the brief asked
for *minimal* and *no over-engineering*. Each is a contained add-on, not a
refactor:

| Concern | Deferred mitigation | Why deferred |
|---|---|---|
| Webhook authenticity | HMAC `X-Signature` middleware (per-provider secret) or mTLS | Brief lists no provider auth contract; flagged as Assumption #3. |
| Transient DB faults | Polly retry policy on `SaveChangesAsync` (Npgsql `IsTransient` predicate) | Single-attempt is acceptable when the provider already retries at-least-once. |
| Latency on the happy path | Single-trip `INSERT … ON CONFLICT DO NOTHING RETURNING` via `ExecuteSqlInterpolated` | SELECT-then-INSERT is two round-trips; the saving is real but not material at the throughput this brief implies. |
| Observability | `System.Diagnostics.Metrics` counters for `ingested_total`, `duplicate_total`, `race_total`, `failed_total{reason}`; OpenTelemetry tracing | Logs cover the screening; without metrics you cannot tell from production whether idempotency is working. |
| Health / readiness | `app.MapHealthChecks("/healthz")` with `AddDbContextCheck<AppDbContext>()` | Deployment-time concern; no orchestrator in scope. |
| HTTPS / transport | `app.UseHttpsRedirection()` and HSTS | Deployment-time concern; local dev runs on HTTP. |
| Rate limiting | `AddRateLimiter` per-source-IP fixed window | Provider behaviour is contractual; not the right control point for unauthenticated dev. |
| Body size limit | Lower from default 30 MB to ~64 KB | A small JSON contract; the default is unsafe but not exploited under the assumed trust model. |
| Configuration secrets | User Secrets locally, env / secret manager in deployed envs | `appsettings.json` ships with dev creds only; never deploy this file. |
| Outbox / fan-out | Transactional outbox + worker for downstream consumers | Named in §"Rejected Alternative" — adopt when a second consumer appears. |
| Test realism | Swap SQLite in-memory for `Testcontainers.PostgreSql` | Would verify Npgsql-specific behaviour (real `SqlState 23505`, real B-tree). The handler-level race test already covers the catch-filter logic with a synthetic exception; Testcontainers would add provider fidelity. Cost ≈ +5 s per CI run. |
| Test project naming | Rename `Fizla.UnitTests` → `Fizla.Tests` (it contains both) | Cosmetic; flagged in this document instead. |

---

## Run it

```bash
docker compose up -d --build               # API on :5080, PG on :5440
dotnet test                                # 26/26 pass
```

`sample-request.http` covers the happy path, an idempotent replay, and an
invalid-payload case for VS Code REST Client / JetBrains HTTP Client.
