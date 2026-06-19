using IdempotentPayments.Api.Contracts;

namespace IdempotentPayments.Api.Services;

public interface IOutboxTransport
{
    Task PublishAsync(OutboxPublishResult message, CancellationToken cancellationToken);
}

public sealed class PermanentPublishException : Exception
{
    public PermanentPublishException(string message)
        : base(message)
    {
    }
}
