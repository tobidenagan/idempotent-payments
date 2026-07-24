using IdempotentPayments.Api.Data;
using IdempotentPayments.Api.Observability;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace IdempotentPayments.Api.Services;

public sealed class OutboxPublisherService : BackgroundService
{
    private readonly WalletRepository _repository;
    private readonly IOutboxTransport _transport;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly OutboxPublisherOptions _options;

    public OutboxPublisherService(
        WalletRepository repository,
        IOutboxTransport transport,
        ILogger<OutboxPublisherService> logger,
        IOptions<OutboxPublisherOptions> options)
    {
        _repository = repository;
        _transport = transport;
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
            _options.MaxAttempts,
            cancellationToken);

        foreach (var message in messages)
        {
            var startedAt = Stopwatch.GetTimestamp();
            using var activity = AppObservability.ActivitySource.StartActivity("outbox.publish");
            activity?.SetTag("messaging.message.id", message.Id);
            activity?.SetTag("messaging.message.type", message.Type);
            activity?.SetTag("messaging.publish.attempt", message.Attempts);

            try
            {
                await _transport.PublishAsync(message, cancellationToken);
                await _repository.MarkOutboxMessageProcessedAsync(message.Id, cancellationToken);
                activity?.SetTag("messaging.publish.result", "Processed");
                AppObservability.OutboxPublishAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("result", "Processed"),
                    new KeyValuePair<string, object?>("event.type", message.Type));
            }
            catch (PermanentPublishException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("messaging.publish.result", "DeadLettered");
                AppObservability.OutboxPublishAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("result", "DeadLettered"),
                    new KeyValuePair<string, object?>("event.type", message.Type));
                _logger.LogError(ex, "Permanently failed outbox message {OutboxMessageId}", message.Id);
                await _repository.DeadLetterOutboxMessageAsync(message.Id, ex.Message, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (message.Attempts >= _options.MaxAttempts)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity?.SetTag("messaging.publish.result", "DeadLettered");
                    AppObservability.OutboxPublishAttempts.Add(
                        1,
                        new KeyValuePair<string, object?>("result", "DeadLettered"),
                        new KeyValuePair<string, object?>("event.type", message.Type));
                    _logger.LogError(
                        ex,
                        "Outbox message {OutboxMessageId} exhausted {Attempts} attempts",
                        message.Id,
                        message.Attempts);

                    await _repository.DeadLetterOutboxMessageAsync(message.Id, ex.Message, cancellationToken);
                    continue;
                }

                var delay = RetryDelayCalculator.Calculate(message.Attempts, _options);
                var nextAttemptAt = DateTimeOffset.UtcNow.Add(delay);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("messaging.publish.result", "RetryScheduled");
                activity?.SetTag("messaging.next_attempt_at", nextAttemptAt);
                AppObservability.OutboxPublishAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("result", "RetryScheduled"),
                    new KeyValuePair<string, object?>("event.type", message.Type));

                _logger.LogWarning(
                    ex,
                    "Outbox message {OutboxMessageId} failed on attempt {Attempt}; retrying at {NextAttemptAt}",
                    message.Id,
                    message.Attempts,
                    nextAttemptAt);

                await _repository.ScheduleOutboxMessageRetryAsync(
                    message.Id,
                    ex.Message,
                    nextAttemptAt,
                    cancellationToken);
            }
            finally
            {
                AppObservability.OperationDuration.Record(
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    new KeyValuePair<string, object?>("operation", "outbox.publish"),
                    new KeyValuePair<string, object?>("event.type", message.Type));
            }
        }
    }
}
