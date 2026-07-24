using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace IdempotentPayments.Api.Observability;

public static class AppObservability
{
    public const string ServiceName = "IdempotentPayments.Api";
    public const string ActivitySourceName = ServiceName;
    public const string MeterName = ServiceName;

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> PaymentAttempts =
        Meter.CreateCounter<long>("payments.attempts", unit: "{attempt}");

    public static readonly Counter<long> WalletDebitAttempts =
        Meter.CreateCounter<long>("wallet.debit.attempts", unit: "{attempt}");

    public static readonly Counter<long> ConsumedEvents =
        Meter.CreateCounter<long>("consumer.events", unit: "{event}");

    public static readonly Counter<long> OutboxPublishAttempts =
        Meter.CreateCounter<long>("outbox.publish.attempts", unit: "{attempt}");

    public static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>("application.operation.duration", unit: "ms");
}
