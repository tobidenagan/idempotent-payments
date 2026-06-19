using IdempotentPayments.Api.Contracts;

namespace IdempotentPayments.Api.Services;

public sealed class LoggingOutboxTransport : IOutboxTransport
{
    private readonly ILogger<LoggingOutboxTransport> _logger;

    public LoggingOutboxTransport(ILogger<LoggingOutboxTransport> logger)
    {
        _logger = logger;
    }

    public Task PublishAsync(OutboxPublishResult message, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Published outbox message {OutboxMessageId} of type {OutboxMessageType}: {Payload}",
            message.Id,
            message.Type,
            message.Payload);

        return Task.CompletedTask;
    }
}
