using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IdempotentPayments.Api.Contracts;
using IdempotentPayments.Api.Data;
using IdempotentPayments.Api.Domain;

namespace IdempotentPayments.Api.Services;

public sealed class PaymentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PaymentRepository _repository;

    public PaymentService(PaymentRepository repository)
    {
        _repository = repository;
    }

    public Task<PaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var normalizedRequest = request with
        {
            Currency = request.Currency.Trim().ToUpperInvariant(),
            CustomerId = request.CustomerId.Trim(),
            IdempotencyKey = request.IdempotencyKey.Trim()
        };

        var requestHash = HashRequest(normalizedRequest);
        return _repository.CreatePaymentIdempotentlyAsync(normalizedRequest, requestHash, cancellationToken);
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
