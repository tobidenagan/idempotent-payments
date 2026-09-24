# Production Readiness Assessment

This repository teaches real payment and distributed-systems mechanisms. It is not yet a production deployment. An implementation is production-ready only after its correctness, scale, security, and operational assumptions have been tested against a stated workload.

## Reconciliation Implemented So Far

- The dashboard query no longer aggregates the entire ledger. It reads the last completed reconciliation result from one PostgreSQL row.
- A background worker scans wallets in batches ordered by wallet ID. The `ledger_entries(wallet_id)` index supports each wallet lookup.
- A transaction-scoped PostgreSQL advisory lock allows only one instance to process a batch at a time. The cursor and cycle counts commit with the batch, so another instance can resume after a crash.
- Each batch uses PostgreSQL Repeatable Read, so a wallet balance and its ledger entries are compared against one transactionally consistent view.
- An incomplete cycle does not replace the last completed result. The `ledger.reconciliation_last_completed` gauge and stale alert expose a scan that never finishes.
- The metrics state publishes an immutable in-process snapshot with one atomic reference exchange. Separate OpenTelemetry gauge callbacks can still observe adjacent snapshots around an update.

## Remaining Scale And Operational Gates

1. A wallet batch bounds the number of wallets, but a single high-volume wallet can have unbounded ledger history. Benchmark this case. If it exceeds the query timeout, move to an incremental ledger projection or a ledger-entry-bounded audit design with an independent verification path.
2. The worker currently shares the API host and database connection pool. Run it as a separate deployment workload, or isolate its connection pool and resource budget, before high-volume production use. The advisory lock already coordinates multiple instances.
3. Persist the affected wallet IDs and observed balances in an access-controlled findings table. A count alone can page an operator but cannot support investigation or reconciliation.
4. Load-test the worker alongside payment traffic. Set batch size, cadence, query timeout, and the stale-cycle threshold from measured database headroom and a stated detection-time objective.
5. Outbox pending and dead-letter counts still scan their indexed subsets every 10 seconds per API instance. Benchmark them under large backlogs; move shared counts to a coordinated collector if needed.

Request-local counters and histograms remain per instance. Shared database-wide gauges are repeated by each API instance, so aggregate them with `max`, not `sum`. Financial decisions must use PostgreSQL transactions and constraints, never telemetry gauges.

## Other Release Gates

- Replace the logging outbox transport with a real broker integration. Prove retry, duplicate delivery, and consumer idempotency behavior against that broker.
- Apply `db/migrations/001_wallet_reconciliation.sql` before enabling the worker outside Development. Add a migration runner and migration verification to the release process; startup schema creation currently runs only in Development.
- Remove development database credentials from deployable configuration; inject secrets through the deployment environment.
- Restrict `/metrics`, PostgreSQL, and Prometheus to trusted networks or authenticated access. The local Compose setup is for development.
- Define customer-facing availability and latency SLIs at the HTTP entry point. The current `payments.attempts` counter misses requests that never reach the service.
- Test concurrency, crash recovery, reconciliation, and alert behavior with multiple API instances and production-sized data before calling the deployment ready.
