using IdempotentPayments.Api.Contracts;

namespace IdempotentPayments.Tests;

public sealed class PaymentValidationTests
{
    [Fact]
    public void ValidRequestPassesValidation()
    {
        var request = new CreatePaymentRequest(5000, "NGN", "cust_123", "idem_123");

        Assert.Null(request.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AmountMustBePositive(long amount)
    {
        var request = new CreatePaymentRequest(amount, "NGN", "cust_123", "idem_123");

        Assert.Equal("Amount must be greater than zero.", request.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("N")]
    [InlineData("NGNN")]
    public void CurrencyMustBeThreeCharacters(string currency)
    {
        var request = new CreatePaymentRequest(5000, currency, "cust_123", "idem_123");

        Assert.Equal("Currency must be a three-letter ISO currency code.", request.Validate());
    }

    [Fact]
    public void CustomerIdIsRequired()
    {
        var request = new CreatePaymentRequest(5000, "NGN", "", "idem_123");

        Assert.Equal("CustomerId is required.", request.Validate());
    }

    [Fact]
    public void IdempotencyKeyIsRequired()
    {
        var request = new CreatePaymentRequest(5000, "NGN", "cust_123", "");

        Assert.Equal("IdempotencyKey is required.", request.Validate());
    }
}
