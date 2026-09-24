namespace IdempotentPayments.Api.Domain;

public sealed record DashboardMetricsSnapshot(
    long PendingOutboxMessages,
    long DeadLetteredOutboxMessages,
    double OldestPendingOutboxAgeSeconds,
    long StaleInProgressIdempotencyKeys,
    long NegativeWalletBalances,
    long WalletLedgerMismatches,
    long ReconciliationLastCompletedUnixSeconds);
