using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace IdempotentPayments.Tests;

public sealed class WalletApiTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("wallet_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Payments"] = _postgres.GetConnectionString()
                    });
                });
            });

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task DebitWithSufficientFundsCreatesLedgerEntryAndUpdatesBalance()
    {
        await CreditAsync("cust_wallet_1", 10000, "fund_1");

        var response = await _client.PostAsJsonAsync(
            "/wallets/cust_wallet_1/debits",
            NewDebit("debit_1", "order_1", 8000));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<WalletDebitResponse>();
        Assert.NotNull(body);
        Assert.StartsWith("wal_", body.WalletId);
        Assert.StartsWith("led_", body.LedgerEntryId);
        Assert.Equal(8000, body.Amount);
        Assert.Equal(2000, body.Balance);
    }

    [Fact]
    public async Task DuplicateDebitReturnsOriginalResponse()
    {
        await CreditAsync("cust_wallet_2", 10000, "fund_2");

        var request = NewDebit("debit_2", "order_2", 4000);
        var first = await _client.PostAsJsonAsync("/wallets/cust_wallet_2/debits", request);
        var second = await _client.PostAsJsonAsync("/wallets/cust_wallet_2/debits", request);

        var firstBody = await first.Content.ReadFromJsonAsync<WalletDebitResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<WalletDebitResponse>();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody, secondBody);
    }

    [Fact]
    public async Task SameDebitIdempotencyKeyWithDifferentPayloadReturnsConflict()
    {
        await CreditAsync("cust_wallet_3", 10000, "fund_3");
        await _client.PostAsJsonAsync("/wallets/cust_wallet_3/debits", NewDebit("debit_3", "order_3", 3000));

        var response = await _client.PostAsJsonAsync(
            "/wallets/cust_wallet_3/debits",
            NewDebit("debit_3", "order_3", 5000));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DebitWithInsufficientFundsReturnsUnprocessableEntity()
    {
        await CreditAsync("cust_wallet_4", 2000, "fund_4");

        var response = await _client.PostAsJsonAsync(
            "/wallets/cust_wallet_4/debits",
            NewDebit("debit_4", "order_4", 8000));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task TwoConcurrentDebitsCannotSpendSameBalanceTwice()
    {
        await CreditAsync("cust_wallet_5", 10000, "fund_5");

        var responses = await Task.WhenAll(
            _client.PostAsJsonAsync("/wallets/cust_wallet_5/debits", NewDebit("debit_5a", "order_5a", 8000)),
            _client.PostAsJsonAsync("/wallets/cust_wallet_5/debits", NewDebit("debit_5b", "order_5b", 8000)));

        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task InsufficientFundsOutcomeIsIdempotent()
    {
        await CreditAsync("cust_wallet_6", 2000, "fund_6");

        var failed = await _client.PostAsJsonAsync(
            "/wallets/cust_wallet_6/debits",
            NewDebit("debit_6", "order_6", 8000));

        await CreditAsync("cust_wallet_6", 10000, "fund_6b");

        var replay = await _client.PostAsJsonAsync(
            "/wallets/cust_wallet_6/debits",
            NewDebit("debit_6", "order_6", 8000));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, replay.StatusCode);
    }

    private async Task CreditAsync(string customerId, long amount, string reference)
    {
        var response = await _client.PostAsJsonAsync(
            $"/wallets/{customerId}/credits",
            new CreditWalletRequest(amount, "USD", reference));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static DebitWalletRequest NewDebit(string idempotencyKey, string reference, long amount) =>
        new(amount, "USD", reference, idempotencyKey);

    private sealed record CreditWalletRequest(long Amount, string Currency, string Reference);

    private sealed record DebitWalletRequest(long Amount, string Currency, string Reference, string IdempotencyKey);

    private sealed record WalletDebitResponse(
        string WalletId,
        string LedgerEntryId,
        string CustomerId,
        long Amount,
        string Currency,
        long Balance,
        string Reference);
}
