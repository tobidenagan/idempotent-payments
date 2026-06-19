using IdempotentPayments.Api.Data;
using Microsoft.Extensions.Options;

namespace IdempotentPayments.Api.Services;

public sealed class OutboxPublisherService : BackgroundService
{
    private readonly WalletRepository _repository;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly OutboxPublisherOptions _options;

    public OutboxPublisherService(
        WalletRepository repository,
        ILogger<OutboxPublisherService> logger,
        IOptions<OutboxPublisherOptions> options)
    {
        _repository = repository;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Outbox publisher is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.IntervalSeconds));

        do
        {
            await PublishPendingMessagesAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PublishPendingMessagesAsync(CancellationToken cancellationToken)
    {
        var messages = await _repository.ClaimPendingOutboxMessagesAsync(
            _options.BatchSize,
            _options.StaleLockSeconds,
            cancellationToken);

        foreach (var message in messages)
        {
            try
            {
                await PublishAsync(message, cancellationToken);
                await _repository.MarkOutboxMessageProcessedAsync(message.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish outbox message {OutboxMessageId}", message.Id);
                await _repository.MarkOutboxMessageFailedAsync(message.Id, ex.Message, cancellationToken);
            }
        }
    }

    private Task PublishAsync(Contracts.OutboxPublishResult message, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Published outbox message {OutboxMessageId} of type {OutboxMessageType}: {Payload}",
            message.Id,
            message.Type,
            message.Payload);

        return Task.CompletedTask;
    }
}
