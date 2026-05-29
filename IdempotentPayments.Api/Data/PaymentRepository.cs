using System.Text.Json;
using IdempotentPayments.Api.Contracts;
using IdempotentPayments.Api.Domain;
using Npgsql;
using NpgsqlTypes;

namespace IdempotentPayments.Api.Data;

public sealed class PaymentRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;

    public PaymentRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create table if not exists payments (
                id text primary key,
                customer_id text not null,
                amount bigint not null check (amount > 0),
                currency char(3) not null,
                status text not null,
                created_at timestamptz not null default now()
            );

            create table if not exists idempotency_keys (
                id bigserial primary key,
                customer_id text not null,
                key text not null,
                request_hash text not null,
                response_body jsonb,
                status_code integer,
                payment_id text references payments(id),
                state text not null,
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                unique (customer_id, key)
            );
            """;

        await using var command = _dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PaymentResult> CreatePaymentIdempotentlyAsync(
        CreatePaymentRequest request,
        string requestHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var inserted = await TryInsertIdempotencyKeyAsync(connection, request, requestHash, cancellationToken);
        if (!inserted)
        {
            var existing = await GetExistingIdempotencyKeyAsync(connection, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (existing.RequestHash != requestHash)
            {
                return new PaymentResult(PaymentResultKind.PayloadMismatch);
            }

            var response = JsonSerializer.Deserialize<PaymentResponse>(existing.ResponseBody!, JsonOptions)
                ?? throw new InvalidOperationException("Stored idempotency response could not be deserialized.");

            return new PaymentResult(PaymentResultKind.Replayed, response);
        }

        var payment = new PaymentResponse(
            PaymentId: $"pay_{Guid.NewGuid():N}",
            Status: "Pending",
            Amount: request.Amount,
            Currency: request.Currency,
            CustomerId: request.CustomerId);

        await InsertPaymentAsync(connection, payment, cancellationToken);
        await CompleteIdempotencyKeyAsync(connection, request, payment, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PaymentResult(PaymentResultKind.Created, payment);
    }

    private static async Task<bool> TryInsertIdempotencyKeyAsync(
        NpgsqlConnection connection,
        CreatePaymentRequest request,
        string requestHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into idempotency_keys (customer_id, key, request_hash, state)
            values (@customer_id, @key, @request_hash, 'InProgress')
            on conflict (customer_id, key) do nothing;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("customer_id", request.CustomerId);
        command.Parameters.AddWithValue("key", request.IdempotencyKey);
        command.Parameters.AddWithValue("request_hash", requestHash);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<ExistingIdempotencyKey> GetExistingIdempotencyKeyAsync(
        NpgsqlConnection connection,
        CreatePaymentRequest request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select request_hash, response_body::text
            from idempotency_keys
            where customer_id = @customer_id and key = @key
            for update;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("customer_id", request.CustomerId);
        command.Parameters.AddWithValue("key", request.IdempotencyKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Idempotency key disappeared during duplicate handling.");
        }

        return new ExistingIdempotencyKey(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task InsertPaymentAsync(
        NpgsqlConnection connection,
        PaymentResponse payment,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into payments (id, customer_id, amount, currency, status)
            values (@id, @customer_id, @amount, @currency, @status);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", payment.PaymentId);
        command.Parameters.AddWithValue("customer_id", payment.CustomerId);
        command.Parameters.AddWithValue("amount", payment.Amount);
        command.Parameters.AddWithValue("currency", payment.Currency);
        command.Parameters.AddWithValue("status", payment.Status);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CompleteIdempotencyKeyAsync(
        NpgsqlConnection connection,
        CreatePaymentRequest request,
        PaymentResponse payment,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update idempotency_keys
            set response_body = @response_body,
                status_code = 201,
                payment_id = @payment_id,
                state = 'Completed',
                updated_at = now()
            where customer_id = @customer_id and key = @key;
            """;

        var responseBody = JsonSerializer.Serialize(payment, JsonOptions);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("response_body", NpgsqlDbType.Jsonb, responseBody);
        command.Parameters.AddWithValue("payment_id", payment.PaymentId);
        command.Parameters.AddWithValue("customer_id", payment.CustomerId);
        command.Parameters.AddWithValue("key", request.IdempotencyKey);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record ExistingIdempotencyKey(string RequestHash, string? ResponseBody);
}
