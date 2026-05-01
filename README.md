# Fizla — Transaction Webhook API

Production-ready ASP.NET Core 8 Web API implementing transaction ingestion via webhook,
organised with Clean Architecture, CQRS-lite, EF Core / PostgreSQL, FluentValidation,
and FluentAssertions / xUnit tests.

> **Design narrative:** see [`SUBMISSION.md`](./SUBMISSION.md) for the
> explanation, assumptions, decision justifications, rejected alternative,
> failure scenario, and production-hardening checklist.

## Layout

```
src/
  Fizla.Domain          // Transaction entity, derived fee rule (no deps)
  Fizla.Application     // Command, validator, handler, abstractions (CQRS-lite)
  Fizla.Infrastructure  // EF Core DbContext, configuration, Npgsql wiring
  Fizla.Api             // Controllers, exception middleware, Program.cs
tests/
  Fizla.UnitTests       // xUnit + FluentAssertions; SQLite in-memory for the integration test
```

## Endpoint

`POST /webhooks/transactions`

### Request

```json
{
  "id": "tx-2026-0001",
  "amount": 250.00,
  "currency": "USD",
  "timestamp": "2026-05-01T10:15:00Z",
  "status": "Completed"
}
```

### Response (200 OK)

```json
{
  "transactionId": "tx-2026-0001",
  "amount": 250.00,
  "fee": 3.75,
  "netAmount": 246.25
}
```

`fee = round(amount * 0.015, 2)` — computed in the domain entity factory and persisted.

## Idempotency

The `transactions` table has a unique index on `external_id`. Replaying the same
payload returns the originally-persisted response with no duplicate row. A
concurrent insert that loses the race surfaces as a `DbUpdateException`
narrowed via `IUniqueViolationDetector` to PostgreSQL `SqlState 23505` only;
the handler then re-reads the winning row and returns it. Any other DB
failure rethrows cleanly. Replays whose payload diverges from the stored
row return the original projection and emit a `Warning` log naming both
sides for operator visibility.

## Run it

```bash
docker compose up -d --build
# api  on http://localhost:5080  (Swagger at /swagger)
# pg   on localhost:5440         (mapped from container :5432)
```

Both services come up in one command. The API container waits for PG's
healthcheck before starting, then runs `Database.Migrate()` on boot, so
the schema is ready immediately. PG is exposed on **5440** to avoid
clashing with any host-side Postgres on 5432.

### Local-only iteration without containers

```bash
docker compose up -d postgres            # bring up PG only
dotnet run --project src/Fizla.Api       # runs against localhost:5440
```

You'll need to update `ConnectionStrings:Postgres` in
`appsettings.Development.json` (or set `ConnectionStrings__Postgres` in
your shell) to point to `Host=localhost;Port=5440;...` for this mode.

### Try it (sample-request.http)

`sample-request.http` includes the happy path, an idempotent replay, and an
invalid-payload case for use in VS Code REST Client / JetBrains HTTP Client.

## Tests

```bash
dotnet test     # 26 tests
```

- `TransactionFeeTests` — domain-level: fee/rounding correctness, entity
  invariants (currency shape, status whitelist, ≤ 2 decimal places,
  non-default `OccurredAt`, non-positive amount), UUID v7 version-bit
  verification.
- `HandlerRaceRecoveryTests` — handler-level: a fake `IAppDbContext`
  throws a synthetic `DbUpdateException(SqliteException(19))` mid-save and
  atomically commits a "winner" row, exercising the unique-violation catch
  filter and the re-read path. Negative control verifies that a
  non-unique-violation `DbUpdateException` propagates instead of being
  swallowed.
- `WebhookIdempotencyTests` — webhook integration over
  `WebApplicationFactory<Program>` + SQLite in-memory: idempotent replay,
  divergent-payload `Warning` log capture, case-sensitive status rejection,
  decimal-places rejection, invalid-payload 400.

## EF Core migrations

The initial migration is checked in under `src/Fizla.Infrastructure/Migrations/`.
To add a new one:

```bash
dotnet ef migrations add <Name> \
  --project src/Fizla.Infrastructure \
  --startup-project src/Fizla.Api
```
