using IdempotentPayments.Api.Contracts;

namespace IdempotentPayments.Api.Domain;

public enum ConsumerResultKind
{
    Processed,
    Duplicate
}

public sealed record ConsumerResult(ConsumerResultKind Kind, ConsumeEventResponse Response);
