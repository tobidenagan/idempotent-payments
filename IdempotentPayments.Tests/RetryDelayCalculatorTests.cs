using IdempotentPayments.Api.Services;

namespace IdempotentPayments.Tests;

public sealed class RetryDelayCalculatorTests
{
    [Fact]
    public void DelayGrowsExponentiallyWithinJitterRange()
    {
        var options = new OutboxPublisherOptions
        {
            BaseDelaySeconds = 10,
            MaxDelaySeconds = 600,
            JitterPercent = 20
        };

        var delay = RetryDelayCalculator.Calculate(3, options);

        Assert.InRange(delay.TotalSeconds, 32, 48);
    }

    [Fact]
    public void DelayNeverExceedsConfiguredMaximum()
    {
        var options = new OutboxPublisherOptions
        {
            BaseDelaySeconds = 10,
            MaxDelaySeconds = 30,
            JitterPercent = 20
        };

        var delay = RetryDelayCalculator.Calculate(10, options);

        Assert.InRange(delay.TotalSeconds, 24, 30);
    }
}
