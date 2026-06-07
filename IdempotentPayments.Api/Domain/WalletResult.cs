using IdempotentPayments.Api.Contracts;

namespace IdempotentPayments.Api.Domain;

public enum WalletResultKind
{
    Created,
    Replayed,
    PayloadMismatch,
    InsufficientFunds
}

public sealed record WalletDebitResult(WalletResultKind Kind, WalletDebitResponse? Response = null);
