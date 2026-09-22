using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using IdempotentPayments.Api.Contracts;
using IdempotentPayments.Api.Data;
using IdempotentPayments.Api.Domain;
using IdempotentPayments.Api.Observability;

namespace IdempotentPayments.Api.Services;

public sealed class PaymentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PaymentRepository _repository;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(PaymentRepository repository, ILogger<PaymentService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<PaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var normalizedRequest = request with
        {
            Currency = request.Currency.Trim().ToUpperInvariant(),
            CustomerId = request.CustomerId.Trim(),
            IdempotencyKey = request.IdempotencyKey.Trim()
        };

        var requestHash = HashRequest(normalizedRequest);

        using var activity = AppObservability.ActivitySource.StartActivity("payment.create");
        activity?.SetTag("payment.currency", normalizedRequest.Currency);

        try
        {
            var result = await _repository.CreatePaymentIdempotentlyAsync(
                normalizedRequest,
                requestHash,
                cancellationToken);

            var resultName = result.Kind.ToString();
            activity?.SetTag("payment.result", resultName);
            activity?.SetTag("payment.id", result.Response?.PaymentId);

            AppObservability.PaymentAttempts.Add(
                1,
                new KeyValuePair<string, object?>("result", resultName),
                new KeyValuePair<string, object?>("currency", normalizedRequest.Currency));

            if (result.Kind == PaymentResultKind.Replayed)
            {
                AppObservability.IdempotencyReplays.Add(
                    1,
                    new KeyValuePair<string, object?>("operation", "payment.create"));
            }
            else if (result.Kind == PaymentResultKind.PayloadMismatch)
            {
                AppObservability.IdempotencyConflicts.Add(
                    1,
                    new KeyValuePair<string, object?>("operation", "payment.create"));

                _logger.LogWarning(
                    "Payment request conflicted for customer {CustomerId} and idempotency key {IdempotencyKey}",
                    normalizedRequest.CustomerId,
                    normalizedRequest.IdempotencyKey);
            }
            else
            {
                _logger.LogInformation(
                    "Payment {PaymentId} returned result {PaymentResult} for customer {CustomerId}",
                    result.Response!.PaymentId,
                    resultName,
                    normalizedRequest.CustomerId);
            }

            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            AppObservability.PaymentAttempts.Add(1, new KeyValuePair<string, object?>("result", "Error"));
            _logger.LogError(ex, "Payment creation failed for customer {CustomerId}", normalizedRequest.CustomerId);
            throw;
        }
        finally
        {
            AppObservability.OperationDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("operation", "payment.create"));
        }
    }

    private static string HashRequest(CreatePaymentRequest request)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            request.Amount,
            request.Currency,
            request.CustomerId
        }, JsonOptions);

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hashBytes);
    }
}
