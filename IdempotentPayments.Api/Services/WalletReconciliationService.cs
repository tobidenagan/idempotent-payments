using IdempotentPayments.Api.Data;
using Microsoft.Extensions.Options;

namespace IdempotentPayments.Api.Services;

public sealed class WalletReconciliationService : BackgroundService
{
    private readonly WalletReconciliationRepository _repository;
    private readonly WalletReconciliationOptions _options;
    private readonly ILogger<WalletReconciliationService> _logger;

    public WalletReconciliationService(
        WalletReconciliationRepository repository,
        IOptions<WalletReconciliationOptions> options,
        ILogger<WalletReconciliationService> logger)
    {
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Wallet reconciliation is disabled.");
            return;
        }

        if (_options.BatchSize <= 0 || _options.IntervalSeconds <= 0 || _options.CommandTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("Wallet reconciliation options must be positive.");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.IntervalSeconds));
        do
        {
            try
            {
                var completed = await _repository.ProcessNextBatchAsync(
                    _options.BatchSize,
                    _options.CommandTimeoutSeconds,
                    stoppingToken);

                if (completed is true)
                {
                    _logger.LogDebug("Wallet reconciliation cycle completed.");
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Wallet reconciliation batch failed; the cursor was not advanced.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
