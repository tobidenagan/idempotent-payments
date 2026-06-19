using IdempotentPayments.Api.Contracts;
using IdempotentPayments.Api.Data;
using IdempotentPayments.Api.Domain;

namespace IdempotentPayments.Api.Services;

public sealed class ConsumerService
{
    private readonly ConsumerRepository _repository;

    public ConsumerService(ConsumerRepository repository)
    {
        _repository = repository;
    }

    public Task<ConsumerResult> ConsumeEventAsync(
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

        return _repository.ProcessEventIdempotentlyAsync(normalizedConsumerName, normalizedRequest, cancellationToken);
    }
}
