namespace IdempotentPayments.Api.Services;

public sealed class OutboxPublisherOptions
{
    public bool Enabled { get; init; }

    public int IntervalSeconds { get; init; } = 5;

    public int BatchSize { get; init; } = 10;

    public int StaleLockSeconds { get; init; } = 300;

    public int MaxAttempts { get; init; } = 10;

    public int BaseDelaySeconds { get; init; } = 5;

    public int MaxDelaySeconds { get; init; } = 900;

    public int JitterPercent { get; init; } = 20;
}
