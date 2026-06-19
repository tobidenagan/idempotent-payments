using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace IdempotentPayments.Tests;

public sealed class ConsumerApiTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("consumer_tests")
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
    public async Task SameConsumerProcessesEventOnce()
    {
        var request = NewEvent("evt_consumer_1");

        var first = await _client.PostAsJsonAsync("/consumers/EmailReceiptConsumer/events", request);
        var second = await _client.PostAsJsonAsync("/consumers/EmailReceiptConsumer/events", request);

        var firstBody = await first.Content.ReadFromJsonAsync<ConsumeEventResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<ConsumeEventResponse>();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("Processed", firstBody!.Status);
        Assert.Equal("DuplicateIgnored", secondBody!.Status);
    }

    [Fact]
    public async Task DifferentConsumersCanProcessSameEvent()
    {
        var request = NewEvent("evt_consumer_2");

        var email = await _client.PostAsJsonAsync("/consumers/EmailReceiptConsumer/events", request);
        var analytics = await _client.PostAsJsonAsync("/consumers/AnalyticsConsumer/events", request);

        Assert.Equal(HttpStatusCode.Created, email.StatusCode);
        Assert.Equal(HttpStatusCode.Created, analytics.StatusCode);
    }

    private static ConsumeEventRequest NewEvent(string eventId) =>
        new(eventId, "WalletDebited", """{"eventId":"evt","amount":8000,"currency":"USD"}""");

    private sealed record ConsumeEventRequest(string EventId, string Type, string Payload);

    private sealed record ConsumeEventResponse(
        string ConsumerName,
        string EventId,
        string Type,
        string Status);
}
