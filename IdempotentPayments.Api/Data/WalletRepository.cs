using System.Text.Json;
using IdempotentPayments.Api.Contracts;
using IdempotentPayments.Api.Domain;
using Npgsql;
using NpgsqlTypes;

namespace IdempotentPayments.Api.Data;

public sealed class WalletRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;

    public WalletRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create table if not exists wallets (
                id text primary key,
                customer_id text not null,
                currency char(3) not null,
                balance bigint not null check (balance >= 0),
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                unique (customer_id, currency)
            );

            create table if not exists ledger_entries (
                id text primary key,
                wallet_id text not null references wallets(id),
                customer_id text not null,
                direction text not null check (direction in ('Credit', 'Debit')),
                amount bigint not null check (amount > 0),
                currency char(3) not null,
                reference text not null,
                created_at timestamptz not null default now(),
                unique (wallet_id, reference, direction)
            );

            create table if not exists outbox_messages (
                id text primary key,
                type text not null,
                payload jsonb not null,
                occurred_at timestamptz not null default now(),
                locked_at timestamptz,
                processed_at timestamptz,
                next_attempt_at timestamptz not null default now(),
                dead_lettered_at timestamptz,
                attempts integer not null default 0,
                last_error text,
                dead_letter_reason text
            );

            alter table outbox_messages
            add column if not exists locked_at timestamptz;

            alter table outbox_messages
            add column if not exists next_attempt_at timestamptz not null default now();

            alter table outbox_messages
            add column if not exists dead_lettered_at timestamptz;

            alter table outbox_messages
            add column if not exists dead_letter_reason text;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<WalletResponse> CreditWalletAsync(
        string customerId,
        CreditWalletRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var wallet = await GetOrCreateWalletAsync(connection, customerId, request.Currency, cancellationToken);
        var newBalance = await AddBalanceAsync(connection, wallet.WalletId, request.Amount, cancellationToken);
        await InsertLedgerEntryAsync(connection, wallet.WalletId, customerId, "Credit", request.Amount, request.Currency, request.Reference, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return wallet with { Balance = newBalance };
    }

    public async Task<WalletDebitResult> DebitWalletIdempotentlyAsync(
        string customerId,
        DebitWalletRequest request,
        string requestHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var inserted = await TryInsertIdempotencyKeyAsync(connection, customerId, request, requestHash, cancellationToken);
        if (!inserted)
        {
            var existing = await GetExistingIdempotencyKeyAsync(connection, customerId, request.IdempotencyKey, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (existing.RequestHash != requestHash)
            {
                return new WalletDebitResult(WalletResultKind.PayloadMismatch);
            }

            if (existing.StatusCode == 422)
            {
                return new WalletDebitResult(WalletResultKind.InsufficientFunds);
            }

            var response = JsonSerializer.Deserialize<WalletDebitResponse>(existing.ResponseBody!, JsonOptions)
                ?? throw new InvalidOperationException("Stored wallet debit response could not be deserialized.");

            return new WalletDebitResult(WalletResultKind.Replayed, response);
        }

        var wallet = await GetOrCreateWalletAsync(connection, customerId, request.Currency, cancellationToken);
        var debited = await TryDebitBalanceAsync(connection, wallet.WalletId, request.Amount, cancellationToken);
        if (debited is null)
        {
            await CompleteFailedIdempotencyKeyAsync(connection, customerId, request.IdempotencyKey, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WalletDebitResult(WalletResultKind.InsufficientFunds);
        }

        var ledgerEntryId = await InsertLedgerEntryAsync(
            connection,
            wallet.WalletId,
            customerId,
            "Debit",
            request.Amount,
            request.Currency,
            request.Reference,
            cancellationToken);

        await InsertWalletDebitedOutboxMessageAsync(
            connection,
            wallet.WalletId,
            ledgerEntryId,
            customerId,
            request.Amount,
            request.Currency,
            debited.Value,
            request.Reference,
            cancellationToken);

        var debitResponse = new WalletDebitResponse(
            wallet.WalletId,
            ledgerEntryId,
            customerId,
            request.Amount,
            request.Currency,
            debited.Value,
            request.Reference);

        await CompleteIdempotencyKeyAsync(connection, customerId, request.IdempotencyKey, debitResponse, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new WalletDebitResult(WalletResultKind.Created, debitResponse);
    }

    public async Task<IReadOnlyList<OutboxMessageResponse>> GetPendingOutboxMessagesAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id, type, payload::text, occurred_at, locked_at, processed_at,
                   next_attempt_at, dead_lettered_at, attempts, last_error, dead_letter_reason
            from outbox_messages
            where processed_at is null
              and dead_lettered_at is null
            order by occurred_at, id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var messages = new List<OutboxMessageResponse>();
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new OutboxMessageResponse(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return messages;
    }

    public async Task<IReadOnlyList<OutboxMessageResponse>> GetDeadLetteredOutboxMessagesAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id, type, payload::text, occurred_at, locked_at, processed_at,
                   next_attempt_at, dead_lettered_at, attempts, last_error, dead_letter_reason
            from outbox_messages
            where dead_lettered_at is not null
            order by dead_lettered_at desc, id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var messages = new List<OutboxMessageResponse>();
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new OutboxMessageResponse(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return messages;
    }

    public async Task<IReadOnlyList<OutboxPublishResult>> ClaimPendingOutboxMessagesAsync(
        int batchSize,
        int staleLockSeconds,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        const string sql = """
            with exhausted_messages as (
                update outbox_messages
                set locked_at = null,
                    dead_lettered_at = now(),
                    dead_letter_reason = coalesce(
                        last_error,
                        'Maximum attempts exhausted after an abandoned claim'
                    )
                where processed_at is null
                  and dead_lettered_at is null
                  and attempts >= @max_attempts
                  and (
                      locked_at is null
                      or locked_at < now() - (@stale_lock_seconds * interval '1 second')
                  )
                returning id
            ),
            next_messages as (
                select id
                from outbox_messages
                where processed_at is null
                  and dead_lettered_at is null
                  and next_attempt_at <= now()
                  and attempts < @max_attempts
                  and (
                      locked_at is null
                      or locked_at < now() - (@stale_lock_seconds * interval '1 second')
                  )
                order by occurred_at, id
                limit @batch_size
                for update skip locked
            )
            update outbox_messages
            set locked_at = now(),
                attempts = attempts + 1
            from next_messages
            where outbox_messages.id = next_messages.id
            returning outbox_messages.id, outbox_messages.type,
                      outbox_messages.payload::text, outbox_messages.attempts;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("batch_size", batchSize);
        command.Parameters.AddWithValue("stale_lock_seconds", staleLockSeconds);
        command.Parameters.AddWithValue("max_attempts", maxAttempts);

        var messages = new List<OutboxPublishResult>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                messages.Add(new OutboxPublishResult(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return messages;
    }

    public async Task MarkOutboxMessageProcessedAsync(
        string messageId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update outbox_messages
            set processed_at = now(),
                locked_at = null,
                last_error = null
            where id = @id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", messageId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ScheduleOutboxMessageRetryAsync(
        string messageId,
        string error,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update outbox_messages
            set locked_at = null,
                next_attempt_at = @next_attempt_at,
                last_error = @last_error
            where id = @id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", messageId);
        command.Parameters.AddWithValue("last_error", error);
        command.Parameters.AddWithValue("next_attempt_at", nextAttemptAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeadLetterOutboxMessageAsync(
        string messageId,
        string reason,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update outbox_messages
            set locked_at = null,
                dead_lettered_at = now(),
                dead_letter_reason = @reason,
                last_error = @reason
            where id = @id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", messageId);
        command.Parameters.AddWithValue("reason", reason);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<WalletResponse> GetOrCreateWalletAsync(
        NpgsqlConnection connection,
        string customerId,
        string currency,
        CancellationToken cancellationToken)
    {
        const string insertSql = """
            insert into wallets (id, customer_id, currency, balance)
            values (@id, @customer_id, @currency, 0)
            on conflict (customer_id, currency) do nothing;
            """;

        await using (var insert = new NpgsqlCommand(insertSql, connection))
        {
            insert.Parameters.AddWithValue("id", $"wal_{Guid.NewGuid():N}");
            insert.Parameters.AddWithValue("customer_id", customerId);
            insert.Parameters.AddWithValue("currency", currency);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        const string selectSql = """
            select id, customer_id, balance, currency
            from wallets
            where customer_id = @customer_id and currency = @currency;
            """;

        await using var select = new NpgsqlCommand(selectSql, connection);
        select.Parameters.AddWithValue("customer_id", customerId);
        select.Parameters.AddWithValue("currency", currency);

        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Wallet could not be created or loaded.");
        }

        return new WalletResponse(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3));
    }

    private static async Task<long> AddBalanceAsync(
        NpgsqlConnection connection,
        string walletId,
        long amount,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update wallets
            set balance = balance + @amount,
                updated_at = now()
            where id = @wallet_id
            returning balance;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("wallet_id", walletId);
        command.Parameters.AddWithValue("amount", amount);

        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Wallet balance could not be credited."));
    }

    private static async Task<long?> TryDebitBalanceAsync(
        NpgsqlConnection connection,
        string walletId,
        long amount,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update wallets
            set balance = balance - @amount,
                updated_at = now()
            where id = @wallet_id
              and balance >= @amount
            returning balance;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("wallet_id", walletId);
        command.Parameters.AddWithValue("amount", amount);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? null : (long)result;
    }

    private static async Task<string> InsertLedgerEntryAsync(
        NpgsqlConnection connection,
        string walletId,
        string customerId,
        string direction,
        long amount,
        string currency,
        string reference,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into ledger_entries (id, wallet_id, customer_id, direction, amount, currency, reference)
            values (@id, @wallet_id, @customer_id, @direction, @amount, @currency, @reference)
            returning id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", $"led_{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("wallet_id", walletId);
        command.Parameters.AddWithValue("customer_id", customerId);
        command.Parameters.AddWithValue("direction", direction);
        command.Parameters.AddWithValue("amount", amount);
        command.Parameters.AddWithValue("currency", currency);
        command.Parameters.AddWithValue("reference", reference);

        return (string)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Ledger entry could not be inserted."));
    }

    private static async Task InsertWalletDebitedOutboxMessageAsync(
        NpgsqlConnection connection,
        string walletId,
        string ledgerEntryId,
        string customerId,
        long amount,
        string currency,
        long balance,
        string reference,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into outbox_messages (id, type, payload)
            values (@id, @type, @payload);
            """;

        var eventId = $"evt_{Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(new
        {
            EventId = eventId,
            Type = "WalletDebited",
            WalletId = walletId,
            LedgerEntryId = ledgerEntryId,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Balance = balance,
            Reference = reference
        }, JsonOptions);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", eventId);
        command.Parameters.AddWithValue("type", "WalletDebited");
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TryInsertIdempotencyKeyAsync(
        NpgsqlConnection connection,
        string customerId,
        DebitWalletRequest request,
        string requestHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into idempotency_keys (customer_id, key, request_hash, state)
            values (@customer_id, @key, @request_hash, 'InProgress')
            on conflict (customer_id, key) do nothing;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("customer_id", customerId);
        command.Parameters.AddWithValue("key", request.IdempotencyKey);
        command.Parameters.AddWithValue("request_hash", requestHash);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<ExistingIdempotencyKey> GetExistingIdempotencyKeyAsync(
        NpgsqlConnection connection,
        string customerId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select request_hash, response_body::text, status_code
            from idempotency_keys
            where customer_id = @customer_id and key = @key
            for update;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("customer_id", customerId);
        command.Parameters.AddWithValue("key", idempotencyKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Idempotency key disappeared during duplicate wallet debit handling.");
        }

        return new ExistingIdempotencyKey(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2));
    }

    private static async Task CompleteFailedIdempotencyKeyAsync(
        NpgsqlConnection connection,
        string customerId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update idempotency_keys
            set response_body = @response_body,
                status_code = 422,
                state = 'Completed',
                updated_at = now()
            where customer_id = @customer_id and key = @key;
            """;

        var responseBody = JsonSerializer.Serialize(new ErrorResponse("Insufficient funds."), JsonOptions);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("response_body", NpgsqlDbType.Jsonb, responseBody);
        command.Parameters.AddWithValue("customer_id", customerId);
        command.Parameters.AddWithValue("key", idempotencyKey);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CompleteIdempotencyKeyAsync(
        NpgsqlConnection connection,
        string customerId,
        string idempotencyKey,
        WalletDebitResponse response,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update idempotency_keys
            set response_body = @response_body,
                status_code = 201,
                state = 'Completed',
                updated_at = now()
            where customer_id = @customer_id and key = @key;
            """;

        var responseBody = JsonSerializer.Serialize(response, JsonOptions);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("response_body", NpgsqlDbType.Jsonb, responseBody);
        command.Parameters.AddWithValue("customer_id", customerId);
        command.Parameters.AddWithValue("key", idempotencyKey);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record ExistingIdempotencyKey(string RequestHash, string? ResponseBody, int StatusCode);
}
