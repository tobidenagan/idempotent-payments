# Idempotent Payments

A small ASP.NET Core Minimal API that demonstrates how to build a payment initiation endpoint with durable idempotency.

The goal of this project is educational: show the production correctness ideas behind retries, duplicate requests, database constraints, and transaction boundaries.

## What This Demonstrates

- ASP.NET Core Minimal APIs
- PostgreSQL-backed idempotency
- Request payload hashing
- Unique constraints for concurrency safety
- Transactional payment creation
- Duplicate request replay
- Conflict handling when an idempotency key is reused with a different payload
- Wallet debit double-spend prevention
- Immutable ledger entries
- Atomic conditional balance updates
- Database outbox for reliable event publishing
- Idempotent event consumers with `processed_messages`

## Project Structure

```text
IdempotentPayments.Api/
  Contracts/        Request and response DTOs
  Data/             PostgreSQL access and transaction logic
  Domain/           Payment result types
  Endpoints/        Minimal API endpoint mapping
  Services/         Application logic and request hashing
  Program.cs        App startup and dependency registration

IdempotentPayments.Tests/
  PaymentValidationTests.cs
  PaymentApiTests.cs
  WalletApiTests.cs
  ConsumerApiTests.cs
```

The most important files to study are:

```text
IdempotentPayments.Api/Data/PaymentRepository.cs
IdempotentPayments.Api/Data/WalletRepository.cs
```

`PaymentRepository.cs` shows durable payment idempotency.

`WalletRepository.cs` shows wallet balance updates, ledger inserts, debit idempotency, and double-spend prevention.

## Run With Docker

Start the API and PostgreSQL:

```powershell
docker compose up --build
```

The API will be available at:

```text
http://localhost:8080
```

Health check:

```powershell
curl http://localhost:8080/health
```

Create a payment:

```powershell
curl -X POST http://localhost:8080/payments `
  -H "Content-Type: application/json" `
  -d "{\"amount\":5000,\"currency\":\"USD\",\"customerId\":\"cust_123\",\"idempotencyKey\":\"abc-123\"}"
```

Send the exact same request again. You should get the same payment response instead of a second payment.

Send the same `idempotencyKey` with a different `amount`. You should get `409 Conflict`.

Stop the containers:

```powershell
docker compose down
```

Stop and remove the PostgreSQL volume:

```powershell
docker compose down -v
```

## Run API + PostgreSQL From Visual Studio

Visual Studio can run both containers using the `docker-compose` project.

In Visual Studio:

1. Open the solution.
2. Right-click `docker-compose` and choose `Set as Startup Project`.
3. Run the `Docker Compose` profile.

This starts both:

```text
api
postgres
```

Use `docker compose up --build` when you want the same API and PostgreSQL setup from the terminal.

The `Docker` launch profile inside `IdempotentPayments.Api` runs only the API container. Prefer the `docker-compose` startup project when you want PostgreSQL to start automatically too.

## Run Without Docker

Start PostgreSQL locally with:

```text
Database: idempotent_payments
Username: postgres
Password: postgres
Port: 5432
```

Then run:

```powershell
dotnet run --project .\IdempotentPayments.Api\IdempotentPayments.Api.csproj
```

The development startup path creates the required tables automatically.

## API Contract

```http
POST /payments
Content-Type: application/json
```

```json
{
  "amount": 5000,
  "currency": "USD",
  "customerId": "cust_123",
  "idempotencyKey": "abc-123"
}
```

Successful response:

```json
{
  "paymentId": "pay_...",
  "status": "Pending",
  "amount": 5000,
  "currency": "USD",
  "customerId": "cust_123"
}
```

## Wallet Debit Demo

The project also includes a wallet debit flow that demonstrates double-spend prevention.

First fund a demo wallet:

```powershell
curl -X POST http://localhost:8080/wallets/cust_123/credits `
  -H "Content-Type: application/json" `
  -d "{\"amount\":10000,\"currency\":\"USD\",\"reference\":\"fund_001\"}"
```

Then debit the wallet:

```powershell
curl -X POST http://localhost:8080/wallets/cust_123/debits `
  -H "Content-Type: application/json" `
  -d "{\"amount\":8000,\"currency\":\"USD\",\"reference\":\"order_123\",\"idempotencyKey\":\"debit_abc\"}"
```

Send the exact same debit again. You should get the same stored debit response.

Send two different `8000 USD` debit requests against a `10000 USD` balance at the same time. Only one should succeed; the other should fail with insufficient funds.

## Idempotency Rules

- First request with a new `customerId + idempotencyKey` creates a payment.
- Same request with the same key returns the stored original response.
- Same key with a different payload returns `409 Conflict`.
- PostgreSQL is the source of truth for idempotency data.
- The database unique constraint protects against concurrent duplicate requests.

## Wallet Correctness Rules

- Wallet balance is a mutable snapshot for fast reads.
- Ledger entries are immutable records of money movement.
- Debits use an atomic conditional update:

```sql
update wallets
set balance = balance - @amount
where id = @wallet_id
  and balance >= @amount
returning balance;
```

- If the update returns no row, the debit failed because the wallet had insufficient funds at the moment of update.
- Wallet update and ledger insert happen in the same transaction.
- Debit attempts are idempotent, including insufficient-funds outcomes.

## Outbox Pattern

Successful wallet debits also insert a `WalletDebited` message into `outbox_messages` inside the same database transaction as:

```text
wallet balance update
ledger entry insert
idempotency completion
```

This prevents the bad state where the wallet debit commits but no durable event exists to publish.

For learning and inspection, pending outbox messages can be read with:

```powershell
curl http://localhost:8080/outbox/pending
```

The project includes a simple background publisher that is disabled by default in `appsettings.json`:

```json
{
  "OutboxPublisher": {
    "Enabled": false,
    "IntervalSeconds": 5,
    "BatchSize": 10,
    "StaleLockSeconds": 300,
    "MaxAttempts": 10,
    "BaseDelaySeconds": 5,
    "MaxDelaySeconds": 900,
    "JitterPercent": 20
  }
}
```

When enabled, the worker claims pending rows, logs a simulated publish, and marks them processed. A real production version would publish to a broker such as RabbitMQ, Kafka, Azure Service Bus, or SQS.

The worker uses a claim step so multiple workers do not process the same rows at the same time. If publishing fails, the row is unlocked and can be retried.

If a worker crashes after claiming a row but before marking it processed or failed, `StaleLockSeconds` allows another worker to reclaim the message later.

Transient failures are retried using capped exponential backoff with jitter. The worker stores the calculated retry time in `next_attempt_at`, so the claim query ignores the row until it is due.

Permanent failures and transient failures that exhaust `MaxAttempts` are dead-lettered. Inspect them with:

```powershell
curl http://localhost:8080/outbox/dead-lettered
```

Dead-lettered messages retain their payload, attempt count, last error, dead-letter time, and reason for investigation or controlled replay.

## Idempotent Consumer Demo

The producer side can publish the same event more than once, so consumers must also be idempotent.

This project includes a teaching endpoint:

```http
POST /consumers/{consumerName}/events
```

Example:

```powershell
curl -X POST http://localhost:8080/consumers/EmailReceiptConsumer/events `
  -H "Content-Type: application/json" `
  -d "{\"eventId\":\"evt_123\",\"type\":\"WalletDebited\",\"payload\":\"{\\\"amount\\\":8000,\\\"currency\\\":\\\"USD\\\"}\"}"
```

The consumer records processed events in `processed_messages` with a unique constraint on:

```text
consumer_name + event_id
```

That means:

- the same consumer ignores duplicate deliveries of the same event
- different consumers can process the same event independently

## Tests

Restore and build:

```powershell
dotnet restore .\IdempotentPayments.slnx --configfile .\NuGet.Config
dotnet build .\IdempotentPayments.Tests\IdempotentPayments.Tests.csproj --no-restore
```

Run tests:

```powershell
dotnet test .\IdempotentPayments.Tests\IdempotentPayments.Tests.csproj --no-build
```

The integration tests in `PaymentApiTests.cs` and `WalletApiTests.cs` require Docker because Testcontainers starts a temporary PostgreSQL container for the test run. Make sure Docker Desktop is running before executing `dotnet test`.

## Learning Path

Read these in order:

1. `ARCHITECTURE.md`
2. `IdempotentPayments.Api/Endpoints/PaymentEndpoints.cs`
3. `IdempotentPayments.Api/Services/PaymentService.cs`
4. `IdempotentPayments.Api/Data/PaymentRepository.cs`
5. `IdempotentPayments.Api/Services/WalletService.cs`
6. `IdempotentPayments.Api/Data/WalletRepository.cs`
7. `IdempotentPayments.Tests/PaymentApiTests.cs`
8. `IdempotentPayments.Tests/WalletApiTests.cs`
9. `IdempotentPayments.Tests/ConsumerApiTests.cs`
