using System.Data;
using Npgsql;

namespace IdempotentPayments.Api.Data;

public sealed class WalletReconciliationRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public WalletReconciliationRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    // Null means another instance owns this batch; true means a full cycle completed.
    public async Task<bool?> ProcessNextBatchAsync(
        int batchSize,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        await using (var lockCommand = new NpgsqlCommand(
            "select pg_try_advisory_xact_lock(941747123456789::bigint);", connection))
        {
            if ((bool)(await lockCommand.ExecuteScalarAsync(cancellationToken) ?? false) is false)
            {
                return null;
            }
        }

        string cursor;
        await using (var stateCommand = new NpgsqlCommand(
            "select last_wallet_id from wallet_reconciliation_state where id = 1 for update;", connection))
        {
            cursor = (string)(await stateCommand.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Wallet reconciliation state is missing."));
        }

        const string batchSql = """
            with wallet_batch as materialized (
                select id, balance
                from wallets
                where id > @cursor
                order by id
                limit @batch_size
            ),
            wallet_checks as (
                select wallet_batch.id,
                       wallet_batch.balance,
                       coalesce((
                           select sum(case direction
                               when 'Credit' then amount
                               when 'Debit' then -amount
                           end)
                           from ledger_entries
                           where wallet_id = wallet_batch.id
                       ), 0) as ledger_balance
                from wallet_batch
            )
            select count(*) as wallets_checked,
                   coalesce(max(id), '') as last_wallet_id,
                   count(*) filter (where balance <> ledger_balance) as mismatches,
                   count(*) filter (where balance < 0) as negative_balances
            from wallet_checks;
            """;

        long walletsChecked;
        string lastWalletId;
        long mismatches;
        long negativeBalances;
        await using (var batchCommand = new NpgsqlCommand(batchSql, connection))
        {
            batchCommand.CommandTimeout = commandTimeoutSeconds;
            batchCommand.Parameters.AddWithValue("cursor", cursor);
            batchCommand.Parameters.AddWithValue("batch_size", batchSize);

            await using var reader = await batchCommand.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            walletsChecked = reader.GetInt64(0);
            lastWalletId = reader.GetString(1);
            mismatches = reader.GetInt64(2);
            negativeBalances = reader.GetInt64(3);
        }

        if (walletsChecked > 0)
        {
            const string advanceSql = """
                update wallet_reconciliation_state
                set last_wallet_id = @last_wallet_id,
                    cycle_mismatches = cycle_mismatches + @mismatches,
                    cycle_negative_balances = cycle_negative_balances + @negative_balances
                where id = 1;
                """;

            await using var advanceCommand = new NpgsqlCommand(advanceSql, connection);
            advanceCommand.Parameters.AddWithValue("last_wallet_id", lastWalletId);
            advanceCommand.Parameters.AddWithValue("mismatches", mismatches);
            advanceCommand.Parameters.AddWithValue("negative_balances", negativeBalances);
            await advanceCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            const string completeSql = """
                update wallet_reconciliation_state
                set completed_mismatches = cycle_mismatches,
                    completed_negative_balances = cycle_negative_balances,
                    last_completed_at = now(),
                    last_wallet_id = '',
                    cycle_mismatches = 0,
                    cycle_negative_balances = 0,
                    cycle_started_at = now()
                where id = 1;
                """;

            await using var completeCommand = new NpgsqlCommand(completeSql, connection);
            await completeCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return walletsChecked == 0;
    }
}
