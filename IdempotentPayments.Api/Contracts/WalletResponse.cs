namespace IdempotentPayments.Api.Contracts;

public sealed record WalletResponse(
    string WalletId,
    string CustomerId,
    long Balance,
    string Currency);

public sealed record WalletDebitResponse(
    string WalletId,
    string LedgerEntryId,
    string CustomerId,
    long Amount,
    string Currency,
    long Balance,
    string Reference);
