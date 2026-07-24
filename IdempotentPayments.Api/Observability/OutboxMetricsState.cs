namespace IdempotentPayments.Api.Observability;

public sealed class OutboxMetricsState
{
    private int _pending;
    private int _deadLettered;

    public OutboxMetricsState()
    {
        AppObservability.Meter.CreateObservableGauge(
            "outbox.pending",
            () => Volatile.Read(ref _pending),
            unit: "{message}");

        AppObservability.Meter.CreateObservableGauge(
            "outbox.dead_lettered",
            () => Volatile.Read(ref _deadLettered),
            unit: "{message}");
    }

    public void Update(int pending, int deadLettered)
    {
        Volatile.Write(ref _pending, pending);
        Volatile.Write(ref _deadLettered, deadLettered);
    }
}
