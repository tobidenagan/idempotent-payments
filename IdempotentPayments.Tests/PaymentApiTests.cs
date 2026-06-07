using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace IdempotentPayments.Tests;

public sealed class PaymentApiTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("idempotent_payments_tests")
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
    public async Task NewRequestCreatesPayment()
    {
        var response = await _client.PostAsJsonAsync("/payments", NewRequest("idem_1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<PaymentResponse>();
        Assert.NotNull(body);
        Assert.StartsWith("pay_", body.PaymentId);
        Assert.Equal("Pending", body.Status);
        Assert.Equal(5000, body.Amount);
        Assert.Equal("USD", body.Currency);
        Assert.Equal("cust_123", body.CustomerId);
    }

    [Fact]
    public async Task SameRequestAndSameIdempotencyKeyReturnsOriginalPayment()
    {
        var first = await _client.PostAsJsonAsync("/payments", NewRequest("idem_2"));
        var second = await _client.PostAsJsonAsync("/payments", NewRequest("idem_2"));

        var firstBody = await first.Content.ReadFromJsonAsync<PaymentResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<PaymentResponse>();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody, secondBody);
    }

    [Fact]
    public async Task SameIdempotencyKeyWithDifferentPayloadReturnsConflict()
    {
        await _client.PostAsJsonAsync("/payments", NewRequest("idem_3"));

        var conflicting = NewRequest("idem_3") with { Amount = 7500 };
        var response = await _client.PostAsJsonAsync("/payments", conflicting);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ConcurrentDuplicateRequestsCreateOnePayment()
    {
        var request = NewRequest("idem_4");

        var responses = await Task.WhenAll(
            _client.PostAsJsonAsync("/payments", request),
            _client.PostAsJsonAsync("/payments", request));

        var bodies = await Task.WhenAll(responses.Select(response =>
            response.Content.ReadFromJsonAsync<PaymentResponse>()));

        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(bodies.Select(body => body!.PaymentId).Distinct());
    }

    [Fact]
    public async Task InvalidRequestReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/payments", NewRequest("idem_5") with { Amount = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static CreatePaymentRequest NewRequest(string idempotencyKey) =>
        new(5000, "USD", "cust_123", idempotencyKey);

    private sealed record CreatePaymentRequest(
        long Amount,
        string Currency,
        string CustomerId,
        string IdempotencyKey);

    private sealed record PaymentResponse(
        string PaymentId,
        string Status,
        long Amount,
        string Currency,
        string CustomerId);
}
