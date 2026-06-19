namespace IdempotentPayments.Api.Contracts;

public sealed record OutboxPublishResult(
    string Id,
    string Type,
    string Payload);
