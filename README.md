# Fizla Transaction Webhook API

ASP.NET Core 8 Web API that ingests transaction events from an external provider, persists them in PostgreSQL, and returns a derived projection (fee + net amount). Built with Clean Architecture, EF Core 8, FluentValidation, and xUnit.

See [SUBMISSION.md](./SUBMISSION.md) for the design narrative: explanation, assumptions, decision justifications, the rejected alternative, the failure scenario, and the production-hardening checklist.

## Layout

```
src/
  Fizla.Domain          Transaction entity, derived fee rule (no deps)
  Fizla.Application     Command, validator, handler, abstractions (CQRS-lite)
  Fizla.Infrastructure  EF Core DbContext, configuration, Npgsql wiring
  Fizla.Api             Controllers, exception middleware, Program.cs
tests/
  Fizla.UnitTests       xUnit + FluentAssertions; SQLite in-memory for the integration tests
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

`fee = round(amount * 0.015, 2)`, computed in the domain entity factory and persisted alongside the raw amount.

## Idempotency

The `transactions` table has a unique index on `external_id`. Replaying the same payload returns the originally persisted response with no duplicate row. A concurrent insert that loses the race surfaces as a `DbUpdateException`, which `IUniqueViolationDetector` narrows to PostgreSQL `SqlState 23505`. The handler then re-reads the winning row and returns it. Any other database failure rethrows.

If a replay carries a different payload under the same id, the handler returns the stored projection and emits a `Warning` log naming both payloads, so operators can spot provider misbehaviour.

## Run it

```bash
docker compose up -d --build
# api on http://localhost:5080  (Swagger at /swagger)
# pg  on localhost:5440         (mapped from container :5432)
```

The API container waits for PostgreSQL's healthcheck before starting, then runs `Database.Migrate()` on boot. PG is exposed on 5440 to avoid clashing with any host-side Postgres on 5432.

### Local-only iteration without containers

```bash
docker compose up -d postgres
dotnet run --project src/Fizla.Api
```

Update `ConnectionStrings:Postgres` in `appsettings.Development.json` (or set `ConnectionStrings__Postgres` in your shell) to `Host=localhost;Port=5440;...` for this mode.

### Try it

`sample-request.http` covers the happy path, an idempotent replay, and an invalid-payload case. Use the VS Code REST Client or JetBrains HTTP Client.

## Tests

```bash
dotnet test     # 26 tests
```

* `TransactionFeeTests`. Domain-level: fee/rounding correctness, entity invariants (currency shape, status whitelist, max two decimal places, non-default `OccurredAt`, non-positive amount), UUID v7 version-bit verification.
* `HandlerRaceRecoveryTests`. Handler-level: a fake `IAppDbContext` throws a synthetic `DbUpdateException(SqliteException(19))` mid-save and atomically commits a "winner" row, exercising the unique-violation catch filter and the re-read path. A negative control verifies that a non-unique-violation `DbUpdateException` propagates instead of being swallowed.
* `WebhookIdempotencyTests`. Webhook integration: idempotent replay, divergent-payload `Warning` log capture, case-sensitive status rejection, decimal-places rejection, invalid-payload 400.

## EF Core migrations

The initial migration is checked in under `src/Fizla.Infrastructure/Migrations/`. Add new ones with:

```bash
dotnet ef migrations add <Name> \
  --project src/Fizla.Infrastructure \
  --startup-project src/Fizla.Api
```
