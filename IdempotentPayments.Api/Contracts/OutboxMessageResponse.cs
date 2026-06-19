namespace IdempotentPayments.Api.Contracts;

public sealed record OutboxMessageResponse(
    string Id,
    string Type,
    string Payload,
    DateTimeOffset OccurredAt,
    DateTimeOffset? LockedAt,
    DateTimeOffset? ProcessedAt,
    int Attempts,
    string? LastError);
