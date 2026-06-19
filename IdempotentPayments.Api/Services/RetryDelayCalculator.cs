namespace IdempotentPayments.Api.Services;

public static class RetryDelayCalculator
{
    public static TimeSpan Calculate(int attempt, OutboxPublisherOptions options)
    {
        var exponent = Math.Max(0, attempt - 1);
        var exponentialSeconds = options.BaseDelaySeconds * Math.Pow(2, exponent);
        var cappedSeconds = Math.Min(exponentialSeconds, options.MaxDelaySeconds);
        var jitterRange = cappedSeconds * options.JitterPercent / 100d;
        var jitter = (Random.Shared.NextDouble() * 2 - 1) * jitterRange;
        var finalSeconds = Math.Min(options.MaxDelaySeconds, Math.Max(1, cappedSeconds + jitter));

        return TimeSpan.FromSeconds(finalSeconds);
    }
}
