# Idempotent Payment Initiation API

This project is a small ASP.NET Core Minimal API that demonstrates payment initiation with durable idempotency.

## Code organization

The project is intentionally small, but it is split into sections so the responsibilities are easy to study:

- `Endpoints`: HTTP request/response handling.
- `Services`: request normalization and request hashing.
- `Data`: PostgreSQL transaction and SQL behavior.
- `Contracts`: request and response models.
- `Domain`: result types used between layers.

## Why idempotency is needed

Payment clients retry requests when they see timeouts, dropped connections, or ambiguous network errors. Without idempotency, a retry can create a second payment for the same user action.

The API accepts an `idempotencyKey` with `POST /payments`. The first request creates a payment. A later request with the same `customerId` and `idempotencyKey` returns the original payment response instead of creating another payment.

## Why the database stores idempotency records

A cache is not durable enough for payment correctness. Cache entries can expire, be evicted, or disappear during failover. This API stores idempotency records in PostgreSQL so the deduplication decision survives process restarts.

The cache can still be added later as an optimization, but PostgreSQL remains the source of truth.

## Database constraints

The important uniqueness rule is:

```sql
unique (customer_id, key)
```

The key is scoped by customer because two different customers may independently generate the same idempotency key. In a merchant/payment-provider system, the scope would often be `merchant_id + key`.

## Transaction boundary

The API writes the idempotency row, payment row, and stored response in one database transaction.

This matters because the system must not commit a payment without the idempotency record that protects retries, and it must not commit a completed idempotency response for a payment that does not exist.

## Duplicate request behavior

If the same request arrives again with the same key, the unique constraint prevents another idempotency row. The API then reads the existing row and returns the stored response.

If the same key is reused with a different payload, the API returns `409 Conflict`. This is detected by hashing a canonical version of the original request payload and comparing it with the stored hash.

## Client timeout and retry

If the client times out after the server commits, the retry returns the original payment response.

If the server fails before the transaction commits, PostgreSQL rolls back the idempotency row and payment row. A retry can safely create the payment.

## Production improvements

- Add structured logs with correlation ID, payment ID, customer ID, idempotency key, status transitions, and failure reasons.
- Return the original status code for replayed responses if the API contract requires exact response replay.
- Add an expiry policy for old idempotency records after the business-safe retention period.
- Add authentication and scope idempotency keys by merchant/account, not only customer.
- Add reconciliation jobs that compare payment records with provider/bank state.
- Add rate limits to protect the endpoint from retry storms.
- Add OpenTelemetry traces and metrics for latency, duplicate requests, conflicts, and database errors.

## Wallet debit correctness

The wallet module adds a second finance-grade lesson: preventing double spend.

The project stores current balance in `wallets` and immutable money movement history in `ledger_entries`.

For debits, the code uses an atomic conditional update:

```sql
update wallets
set balance = balance - @amount,
    updated_at = now()
where id = @wallet_id
  and balance >= @amount
returning balance;
```

This keeps the balance check and deduction in one database statement. If two `8000 USD` debits race against a `10000 USD` balance, one update can succeed and the other returns no row.

The debit flow also writes a ledger entry in the same transaction. This prevents bad states such as balance changed without a ledger entry, or a ledger entry existing without the balance change.

Debit idempotency follows the same model as payment idempotency:

- same key and same payload replays the stored response
- same key and different payload returns conflict
- insufficient funds is stored as an idempotent outcome

## Outbox pattern

The wallet debit flow inserts a `WalletDebited` message into `outbox_messages` in the same transaction as the wallet update, ledger insert, and idempotency completion.

This handles the database side of the outbox pattern:

```text
begin transaction
claim idempotency key
debit wallet
insert ledger entry
insert outbox message
complete idempotency key
commit
```

If the transaction commits, both the debit and the intent to publish an event exist. If the transaction rolls back, neither exists.

The outbox does not guarantee exactly-once delivery. A later publisher can still send the same event more than once if it crashes after publishing but before marking the outbox message as processed. Consumers must be idempotent, usually by storing processed event IDs with a unique constraint.

## Background publisher

The project includes a simple hosted service, `OutboxPublisherService`, disabled by default.

When enabled, it:

```text
claims pending outbox rows
publishes each message
marks successful messages processed
unlocks failed messages for retry
```

The claim query uses `FOR UPDATE SKIP LOCKED` plus a `locked_at` marker. `SKIP LOCKED` prevents concurrent workers from claiming the same row during the claim transaction, and `locked_at` keeps the row reserved after the claim transaction commits.

In this teaching project, publishing is simulated with a structured log line. In a production system, `PublishAsync` would send to a real broker.

The claim query also supports stale lock recovery:

```sql
locked_at is null
or locked_at < now() - (@stale_lock_seconds * interval '1 second')
```

This prevents a message from being stuck forever if a worker crashes after claiming it but before marking it processed or failed.

## Retry and dead-letter policy

Transient publish failures are scheduled using exponential backoff with jitter:

```text
base delay * 2^(attempt - 1)
apply random jitter
cap at maximum delay
```

The calculated time is stored in `next_attempt_at`. The claim query selects only rows where `next_attempt_at <= now()`.

Permanent failures are dead-lettered immediately. Transient failures are dead-lettered after `MaxAttempts`. Dead-letter state is recorded with:

```text
dead_lettered_at
dead_letter_reason
last_error
attempts
```

An exhausted stale claim is also dead-lettered during the next claim cycle. This covers a worker crashing after claiming the final allowed attempt.

## Idempotent consumers

The consumer side uses `processed_messages` to prevent duplicate event side effects.

The important uniqueness rule is:

```sql
unique (consumer_name, event_id)
```

This lets each consumer process an event once for itself:

```text
EmailReceiptConsumer + evt_123 -> processed once
AnalyticsConsumer + evt_123 -> processed once
EmailReceiptConsumer + evt_123 again -> duplicate ignored
```

This is the consumer-side companion to the outbox pattern. The outbox gives at-least-once publishing, and `processed_messages` makes duplicate deliveries safe.
