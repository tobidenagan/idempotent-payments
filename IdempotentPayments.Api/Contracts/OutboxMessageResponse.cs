namespace IdempotentPayments.Api.Contracts;

public sealed record OutboxMessageResponse(
    string Id,
    string Type,
    string Payload,
    DateTimeOffset OccurredAt,
    DateTimeOffset? LockedAt,
    DateTimeOffset? ProcessedAt,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? DeadLetteredAt,
    int Attempts,
    string? LastError,
    string? DeadLetterReason);
