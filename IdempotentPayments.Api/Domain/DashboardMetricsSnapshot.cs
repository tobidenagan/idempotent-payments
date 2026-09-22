namespace IdempotentPayments.Api.Domain;

public sealed record DashboardMetricsSnapshot(
    int PendingOutboxMessages,
    int DeadLetteredOutboxMessages,
    double OldestPendingOutboxAgeSeconds,
    int StaleInProgressIdempotencyKeys,
    int NegativeWalletBalances,
    int WalletLedgerMismatches);
