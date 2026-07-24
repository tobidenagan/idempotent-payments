using IdempotentPayments.Api.Data;

namespace IdempotentPayments.Api.Observability;

public sealed class OutboxMetricsCollectorService : BackgroundService
{
    private readonly WalletRepository _repository;
    private readonly OutboxMetricsState _state;
    private readonly ILogger<OutboxMetricsCollectorService> _logger;

    public OutboxMetricsCollectorService(
        WalletRepository repository,
        OutboxMetricsState state,
        ILogger<OutboxMetricsCollectorService> logger)
    {
        _repository = repository;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        do
        {
            try
            {
                var counts = await _repository.GetOutboxCountsAsync(stoppingToken);
                _state.Update(counts.Pending, counts.DeadLettered);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Failed to collect outbox metrics");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
