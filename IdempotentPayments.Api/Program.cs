using IdempotentPayments.Api.Data;
using IdempotentPayments.Api.Endpoints;
using IdempotentPayments.Api.Observability;
using IdempotentPayments.Api.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

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
builder.Services.AddSingleton<WalletReconciliationRepository>();
builder.Services.AddSingleton<WalletService>();
builder.Services.AddSingleton<ConsumerRepository>();
builder.Services.AddSingleton<ConsumerService>();
builder.Services.AddSingleton<IOutboxTransport, LoggingOutboxTransport>();
builder.Services.AddSingleton<OutboxMetricsState>();
builder.Services.Configure<OutboxPublisherOptions>(builder.Configuration.GetSection("OutboxPublisher"));
builder.Services.Configure<WalletReconciliationOptions>(builder.Configuration.GetSection("WalletReconciliation"));
builder.Services.AddHostedService<OutboxPublisherService>();
builder.Services.AddHostedService<OutboxMetricsCollectorService>();
builder.Services.AddHostedService<WalletReconciliationService>();

builder.Services.AddHealthChecks()
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddCheck<PostgresHealthCheck>("postgres", tags: new[] { "ready" });

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(AppObservability.ServiceName))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation(options =>
        {
            options.Filter = context => !context.Request.Path.StartsWithSegments("/health");
        })
        .AddSource(AppObservability.ActivitySourceName)
        .AddSource("Npgsql");

        if (builder.Environment.IsDevelopment())
        {
            tracing.AddConsoleExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation()
            .AddMeter(AppObservability.MeterName)
            .AddPrometheusExporter();

        if (builder.Environment.IsDevelopment())
        {
            metrics.AddConsoleExporter();
        }
    });

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();

app.MapPaymentEndpoints();
app.MapWalletEndpoints();
app.MapConsumerEndpoints();

var livenessOptions = new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
    ResponseWriter = HealthResponseWriter.WriteAsync
};

var readinessOptions = new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthResponseWriter.WriteAsync
};

app.MapHealthChecks("/health/live", livenessOptions);
app.MapHealthChecks("/health/ready", readinessOptions);
app.MapHealthChecks("/health", readinessOptions);
app.MapPrometheusScrapingEndpoint();

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
