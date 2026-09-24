namespace IdempotentPayments.Api.Services;

public sealed class WalletReconciliationOptions
{
    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 500;
    public int IntervalSeconds { get; set; } = 5;
    public int CommandTimeoutSeconds { get; set; } = 10;
}
