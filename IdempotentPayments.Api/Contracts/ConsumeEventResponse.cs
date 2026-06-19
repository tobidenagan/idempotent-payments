namespace IdempotentPayments.Api.Contracts;

public sealed record ConsumeEventResponse(
    string ConsumerName,
    string EventId,
    string Type,
    string Status);
