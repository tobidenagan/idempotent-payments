using IdempotentPayments.Api.Data;
using IdempotentPayments.Api.Endpoints;
using IdempotentPayments.Api.Services;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("Payments")
        ?? "Host=localhost;Port=5432;Database=idempotent_payments;Username=postgres;Password=postgres";

    return NpgsqlDataSource.Create(connectionString);
});

builder.Services.AddSingleton<PaymentRepository>();
builder.Services.AddSingleton<PaymentService>();
builder.Services.AddSingleton<WalletRepository>();
builder.Services.AddSingleton<WalletService>();
builder.Services.AddSingleton<ConsumerRepository>();
builder.Services.AddSingleton<ConsumerService>();
builder.Services.AddSingleton<IOutboxTransport, LoggingOutboxTransport>();
builder.Services.Configure<OutboxPublisherOptions>(builder.Configuration.GetSection("OutboxPublisher"));
builder.Services.AddHostedService<OutboxPublisherService>();

var app = builder.Build();

app.MapPaymentEndpoints();
app.MapWalletEndpoints();
app.MapConsumerEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var repository = scope.ServiceProvider.GetRequiredService<PaymentRepository>();
    await repository.EnsureSchemaAsync(CancellationToken.None);

    var walletRepository = scope.ServiceProvider.GetRequiredService<WalletRepository>();
    await walletRepository.EnsureSchemaAsync(CancellationToken.None);

    var consumerRepository = scope.ServiceProvider.GetRequiredService<ConsumerRepository>();
    await consumerRepository.EnsureSchemaAsync(CancellationToken.None);
}

app.Run();

public partial class Program;
