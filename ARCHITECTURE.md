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
