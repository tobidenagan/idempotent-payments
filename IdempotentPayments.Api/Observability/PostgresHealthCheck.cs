using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace IdempotentPayments.Api.Observability;

public sealed class PostgresHealthCheck : IHealthCheck
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresHealthCheck(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = _dataSource.CreateCommand("select 1;");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy("PostgreSQL is reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.", ex);
        }
    }
}
