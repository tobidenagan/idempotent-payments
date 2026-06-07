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
