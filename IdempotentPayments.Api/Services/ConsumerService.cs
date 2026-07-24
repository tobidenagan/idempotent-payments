using IdempotentPayments.Api.Contracts;
using IdempotentPayments.Api.Data;
using IdempotentPayments.Api.Domain;
using IdempotentPayments.Api.Observability;

namespace IdempotentPayments.Api.Services;

public sealed class ConsumerService
{
    private readonly ConsumerRepository _repository;
    private readonly ILogger<ConsumerService> _logger;

    public ConsumerService(ConsumerRepository repository, ILogger<ConsumerService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<ConsumerResult> ConsumeEventAsync(
        string consumerName,
        ConsumeEventRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedConsumerName = consumerName.Trim();
        var normalizedRequest = request with
        {
            EventId = request.EventId.Trim(),
            Type = request.Type.Trim(),
            Payload = request.Payload.Trim()
        };

        using var activity = AppObservability.ActivitySource.StartActivity("consumer.process");
        activity?.SetTag("messaging.event.id", normalizedRequest.EventId);
        activity?.SetTag("messaging.event.type", normalizedRequest.Type);
        activity?.SetTag("messaging.consumer", normalizedConsumerName);

        var result = await _repository.ProcessEventIdempotentlyAsync(
            normalizedConsumerName,
            normalizedRequest,
            cancellationToken);

        var resultName = result.Kind.ToString();
        activity?.SetTag("messaging.consumer.result", resultName);
        AppObservability.ConsumedEvents.Add(
            1,
            new KeyValuePair<string, object?>("result", resultName),
            new KeyValuePair<string, object?>("event.type", normalizedRequest.Type));

        _logger.LogInformation(
            "Consumer {ConsumerName} returned result {ConsumerResult} for event {EventId} of type {EventType}",
            normalizedConsumerName,
            resultName,
            normalizedRequest.EventId,
            normalizedRequest.Type);

        return result;
    }
}
