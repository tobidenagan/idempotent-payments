-- Run with psql outside an explicit transaction before deploying the worker.
create index concurrently if not exists ledger_entries_wallet_id_idx
on ledger_entries (wallet_id);

create index concurrently if not exists outbox_pending_occurred_at_idx
on outbox_messages (occurred_at)
where processed_at is null and dead_lettered_at is null;

create index concurrently if not exists outbox_dead_lettered_at_idx
on outbox_messages (dead_lettered_at)
where dead_lettered_at is not null;

create index concurrently if not exists idempotency_in_progress_updated_at_idx
on idempotency_keys (updated_at)
where state = 'InProgress';

create table if not exists wallet_reconciliation_state (
    id smallint primary key check (id = 1),
    last_wallet_id text not null default '',
    cycle_mismatches bigint not null default 0,
    cycle_negative_balances bigint not null default 0,
    completed_mismatches bigint not null default 0,
    completed_negative_balances bigint not null default 0,
    last_completed_at timestamptz,
    cycle_started_at timestamptz not null default now()
);

insert into wallet_reconciliation_state (id)
values (1)
on conflict (id) do nothing;
