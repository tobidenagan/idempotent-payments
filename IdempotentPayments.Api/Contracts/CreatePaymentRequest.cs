namespace IdempotentPayments.Api.Contracts;

public sealed record CreatePaymentRequest(
    long Amount,
    string Currency,
    string CustomerId,
    string IdempotencyKey)
{
    public string? Validate()
    {
        if (Amount <= 0)
        {
            return "Amount must be greater than zero.";
        }

        if (string.IsNullOrWhiteSpace(Currency) || Currency.Trim().Length != 3)
        {
            return "Currency must be a three-letter ISO currency code.";
        }

        if (string.IsNullOrWhiteSpace(CustomerId))
        {
            return "CustomerId is required.";
        }

        if (string.IsNullOrWhiteSpace(IdempotencyKey))
        {
            return "IdempotencyKey is required.";
        }

        return null;
    }
}
