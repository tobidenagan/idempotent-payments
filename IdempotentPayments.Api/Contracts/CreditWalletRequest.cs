namespace IdempotentPayments.Api.Contracts;

public sealed record CreditWalletRequest(
    long Amount,
    string Currency,
    string Reference)
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

        if (string.IsNullOrWhiteSpace(Reference))
        {
            return "Reference is required.";
        }

        return null;
    }
}
