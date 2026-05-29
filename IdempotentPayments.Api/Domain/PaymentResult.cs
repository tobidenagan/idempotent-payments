using IdempotentPayments.Api.Contracts;

namespace IdempotentPayments.Api.Domain;

public enum PaymentResultKind
{
    Created,
    Replayed,
    PayloadMismatch
}

public sealed record PaymentResult(PaymentResultKind Kind, PaymentResponse? Response = null);
