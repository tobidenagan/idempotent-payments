namespace IdempotentPayments.Api.Contracts;

public sealed record PaymentResponse(
    string PaymentId,
    string Status,
    long Amount,
    string Currency,
    string CustomerId);
