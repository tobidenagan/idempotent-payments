using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IdempotentPayments.Api.Contracts;
using IdempotentPayments.Api.Data;
using IdempotentPayments.Api.Domain;

namespace IdempotentPayments.Api.Services;

public sealed class WalletService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WalletRepository _repository;

    public WalletService(WalletRepository repository)
    {
        _repository = repository;
    }

    public Task<WalletResponse> CreditWalletAsync(
        string customerId,
        CreditWalletRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedCustomerId = customerId.Trim();
        var normalizedRequest = request with
        {
            Currency = request.Currency.Trim().ToUpperInvariant(),
            Reference = request.Reference.Trim()
        };

        return _repository.CreditWalletAsync(normalizedCustomerId, normalizedRequest, cancellationToken);
    }

    public Task<WalletDebitResult> DebitWalletAsync(
        string customerId,
        DebitWalletRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedCustomerId = customerId.Trim();
        var normalizedRequest = request with
        {
            Currency = request.Currency.Trim().ToUpperInvariant(),
            Reference = request.Reference.Trim(),
            IdempotencyKey = request.IdempotencyKey.Trim()
        };

        var requestHash = HashDebitRequest(normalizedCustomerId, normalizedRequest);
        return _repository.DebitWalletIdempotentlyAsync(normalizedCustomerId, normalizedRequest, requestHash, cancellationToken);
    }

    public Task<IReadOnlyList<OutboxMessageResponse>> GetPendingOutboxMessagesAsync(
        CancellationToken cancellationToken)
    {
        return _repository.GetPendingOutboxMessagesAsync(cancellationToken);
    }

    private static string HashDebitRequest(string customerId, DebitWalletRequest request)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            CustomerId = customerId,
            request.Amount,
            request.Currency,
            request.Reference
        }, JsonOptions);

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hashBytes);
    }
}
