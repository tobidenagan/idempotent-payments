using IdempotentPayments.Api.Contracts;
using IdempotentPayments.Api.Domain;
using Npgsql;

namespace IdempotentPayments.Api.Data;

public sealed class ConsumerRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public ConsumerRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create table if not exists processed_messages (
                id bigserial primary key,
                consumer_name text not null,
                event_id text not null,
                type text not null,
                payload text not null,
                processed_at timestamptz not null default now(),
                unique (consumer_name, event_id)
            );
            """;

        await using var command = _dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ConsumerResult> ProcessEventIdempotentlyAsync(
        string consumerName,
        ConsumeEventRequest request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into processed_messages (consumer_name, event_id, type, payload)
            values (@consumer_name, @event_id, @type, @payload)
            on conflict (consumer_name, event_id) do nothing;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("consumer_name", consumerName);
        command.Parameters.AddWithValue("event_id", request.EventId);
        command.Parameters.AddWithValue("type", request.Type);
        command.Parameters.AddWithValue("payload", request.Payload);

        var inserted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        var status = inserted ? "Processed" : "DuplicateIgnored";

        return new ConsumerResult(
            inserted ? ConsumerResultKind.Processed : ConsumerResultKind.Duplicate,
            new ConsumeEventResponse(consumerName, request.EventId, request.Type, status));
    }
}
