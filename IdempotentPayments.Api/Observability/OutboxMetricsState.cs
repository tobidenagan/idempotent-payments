using IdempotentPayments.Api.Domain;

namespace IdempotentPayments.Api.Observability;

public sealed class OutboxMetricsState
{
    private PublishedMetrics _snapshot = new(
        new DashboardMetricsSnapshot(0, 0, 0, 0, 0, 0, 0), 0);

    public OutboxMetricsState()
    {
        AppObservability.Meter.CreateObservableGauge(
            "outbox.pending",
            () => Volatile.Read(ref _snapshot).Values.PendingOutboxMessages,
            unit: "{message}");

        AppObservability.Meter.CreateObservableGauge(
            "outbox.dead_lettered",
            () => Volatile.Read(ref _snapshot).Values.DeadLetteredOutboxMessages,
            unit: "{message}");

        AppObservability.Meter.CreateObservableGauge(
            "outbox.oldest_pending_age",
            () => Volatile.Read(ref _snapshot).Values.OldestPendingOutboxAgeSeconds,
            unit: "s");

        AppObservability.Meter.CreateObservableGauge(
            "idempotency.in_progress_stale",
            () => Volatile.Read(ref _snapshot).Values.StaleInProgressIdempotencyKeys,
            unit: "{key}");

        AppObservability.Meter.CreateObservableGauge(
            "wallet.negative_balance",
            () => Volatile.Read(ref _snapshot).Values.NegativeWalletBalances,
            unit: "{wallet}");

        AppObservability.Meter.CreateObservableGauge(
            "ledger.wallet_mismatch",
            () => Volatile.Read(ref _snapshot).Values.WalletLedgerMismatches,
            unit: "{wallet}");

        AppObservability.Meter.CreateObservableGauge(
            "ledger.reconciliation_last_completed",
            () => Volatile.Read(ref _snapshot).Values.ReconciliationLastCompletedUnixSeconds,
            unit: "s");

        AppObservability.Meter.CreateObservableGauge(
            "dashboard_metrics.last_success",
            () => Volatile.Read(ref _snapshot).CollectedAtUnixSeconds,
            unit: "s");
    }

    public void Update(DashboardMetricsSnapshot values)
    {
        Interlocked.Exchange(
            ref _snapshot,
            new PublishedMetrics(values, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    private sealed record PublishedMetrics(
        DashboardMetricsSnapshot Values,
        long CollectedAtUnixSeconds);
}
