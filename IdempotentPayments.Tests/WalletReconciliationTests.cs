using IdempotentPayments.Api.Contracts;
using IdempotentPayments.Api.Data;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IdempotentPayments.Tests;

public sealed class WalletReconciliationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("reconciliation_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private NpgsqlDataSource _dataSource = null!;
    private WalletRepository _wallets = null!;
    private WalletReconciliationRepository _reconciliation = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await new PaymentRepository(_dataSource).EnsureSchemaAsync(CancellationToken.None);
        _wallets = new WalletRepository(_dataSource);
        await _wallets.EnsureSchemaAsync(CancellationToken.None);
        _reconciliation = new WalletReconciliationRepository(_dataSource);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task PublishesOnlyCompletedCycleAndResumesAcrossRepositoryInstances()
    {
        var wallet = await _wallets.CreditWalletAsync(
            "cust_reconcile",
            new CreditWalletRequest(1000, "USD", "fund_reconcile"),
            CancellationToken.None);

        await using (var command = _dataSource.CreateCommand(
            "update wallets set balance = 900 where id = @id;"))
        {
            command.Parameters.AddWithValue("id", wallet.WalletId);
            await command.ExecuteNonQueryAsync();
        }

        Assert.False(await _reconciliation.ProcessNextBatchAsync(1, 10, CancellationToken.None));

        var incomplete = await _wallets.GetDashboardMetricsAsync(CancellationToken.None);
        Assert.Equal(0, incomplete.WalletLedgerMismatches);
        Assert.Equal(0, incomplete.ReconciliationLastCompletedUnixSeconds);

        var secondInstance = new WalletReconciliationRepository(_dataSource);
        Assert.True(await secondInstance.ProcessNextBatchAsync(1, 10, CancellationToken.None));

        var completed = await _wallets.GetDashboardMetricsAsync(CancellationToken.None);
        Assert.Equal(1, completed.WalletLedgerMismatches);
        Assert.True(completed.ReconciliationLastCompletedUnixSeconds > 0);

        await using (var command = _dataSource.CreateCommand(
            "update wallets set balance = 1000 where id = @id;"))
        {
            command.Parameters.AddWithValue("id", wallet.WalletId);
            await command.ExecuteNonQueryAsync();
        }

        Assert.False(await _reconciliation.ProcessNextBatchAsync(1, 10, CancellationToken.None));
        Assert.Equal(1, (await _wallets.GetDashboardMetricsAsync(CancellationToken.None)).WalletLedgerMismatches);
        Assert.True(await secondInstance.ProcessNextBatchAsync(1, 10, CancellationToken.None));
        Assert.Equal(0, (await _wallets.GetDashboardMetricsAsync(CancellationToken.None)).WalletLedgerMismatches);
    }

    [Fact]
    public async Task AnotherInstanceSkipsBatchWhileLeaseIsHeld()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            "select pg_advisory_xact_lock(941747123456789::bigint);", connection);
        await command.ExecuteNonQueryAsync();

        var result = await _reconciliation.ProcessNextBatchAsync(1, 10, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task OneWalletBatchesAdvanceCursorWithoutSkippingOrDoubleCounting()
    {
        var wallets = new List<WalletResponse>();
        for (var index = 0; index < 3; index++)
        {
            wallets.Add(await _wallets.CreditWalletAsync(
                $"cust_batch_{index}",
                new CreditWalletRequest(1000, "USD", $"fund_batch_{index}"),
                CancellationToken.None));
        }

        await using (var command = _dataSource.CreateCommand(
            "update wallets set balance = 900 where id = @id;"))
        {
            command.Parameters.AddWithValue("id", wallets[1].WalletId);
            await command.ExecuteNonQueryAsync();
        }

        long walletCount;
        await using (var command = _dataSource.CreateCommand("select count(*) from wallets;"))
        {
            walletCount = (long)(await command.ExecuteScalarAsync() ?? 0L);
        }

        for (var index = 0L; index < walletCount; index++)
        {
            Assert.False(await _reconciliation.ProcessNextBatchAsync(1, 10, CancellationToken.None));
        }

        Assert.True(await _reconciliation.ProcessNextBatchAsync(1, 10, CancellationToken.None));
        Assert.Equal(1L, (await _wallets.GetDashboardMetricsAsync(CancellationToken.None)).WalletLedgerMismatches);
    }
}
