namespace IdempotentPayments.Api.Observability;

public sealed class OutboxMetricsState
{
    private int _pending;
    private int _deadLettered;
    private double _oldestPendingAgeSeconds;
    private int _staleInProgressIdempotencyKeys;
    private int _negativeWalletBalances;
    private int _walletLedgerMismatches;

    public OutboxMetricsState()
    {
        AppObservability.Meter.CreateObservableGauge(
            "outbox.pending",
            () => Volatile.Read(ref _pending),
            unit: "{message}");

        AppObservability.Meter.CreateObservableGauge(
            "outbox.dead_lettered",
            () => Volatile.Read(ref _deadLettered),
            unit: "{message}");

        AppObservability.Meter.CreateObservableGauge(
            "outbox.oldest_pending_age",
            () => Volatile.Read(ref _oldestPendingAgeSeconds),
            unit: "s");

        AppObservability.Meter.CreateObservableGauge(
            "idempotency.in_progress_stale",
            () => Volatile.Read(ref _staleInProgressIdempotencyKeys),
            unit: "{key}");

        AppObservability.Meter.CreateObservableGauge(
            "wallet.negative_balance",
            () => Volatile.Read(ref _negativeWalletBalances),
            unit: "{wallet}");

        AppObservability.Meter.CreateObservableGauge(
            "ledger.wallet_mismatch",
            () => Volatile.Read(ref _walletLedgerMismatches),
            unit: "{wallet}");
    }

    public void Update(
        int pending,
        int deadLettered,
        double oldestPendingAgeSeconds,
        int staleInProgressIdempotencyKeys,
        int negativeWalletBalances,
        int walletLedgerMismatches)
    {
        Volatile.Write(ref _pending, pending);
        Volatile.Write(ref _deadLettered, deadLettered);
        Volatile.Write(ref _oldestPendingAgeSeconds, oldestPendingAgeSeconds);
        Volatile.Write(ref _staleInProgressIdempotencyKeys, staleInProgressIdempotencyKeys);
        Volatile.Write(ref _negativeWalletBalances, negativeWalletBalances);
        Volatile.Write(ref _walletLedgerMismatches, walletLedgerMismatches);
    }
}
