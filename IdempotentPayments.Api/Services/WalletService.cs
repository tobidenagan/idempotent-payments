using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using IdempotentPayments.Api.Contracts;
using IdempotentPayments.Api.Data;
using IdempotentPayments.Api.Domain;
using IdempotentPayments.Api.Observability;

namespace IdempotentPayments.Api.Services;

public sealed class WalletService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WalletRepository _repository;
    private readonly ILogger<WalletService> _logger;

    public WalletService(WalletRepository repository, ILogger<WalletService> logger)
    {
        _repository = repository;
        _logger = logger;
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

    public async Task<WalletDebitResult> DebitWalletAsync(
        string customerId,
        DebitWalletRequest request,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var normalizedCustomerId = customerId.Trim();
        var normalizedRequest = request with
        {
            Currency = request.Currency.Trim().ToUpperInvariant(),
            Reference = request.Reference.Trim(),
            IdempotencyKey = request.IdempotencyKey.Trim()
        };

        var requestHash = HashDebitRequest(normalizedCustomerId, normalizedRequest);

        using var activity = AppObservability.ActivitySource.StartActivity("wallet.debit");
        activity?.SetTag("wallet.currency", normalizedRequest.Currency);

        try
        {
            var result = await _repository.DebitWalletIdempotentlyAsync(
                normalizedCustomerId,
                normalizedRequest,
                requestHash,
                cancellationToken);

            var resultName = result.Kind.ToString();
            activity?.SetTag("wallet.debit.result", resultName);
            activity?.SetTag("wallet.id", result.Response?.WalletId);
            activity?.SetTag("ledger.entry.id", result.Response?.LedgerEntryId);

            AppObservability.WalletDebitAttempts.Add(
                1,
                new KeyValuePair<string, object?>("result", resultName),
                new KeyValuePair<string, object?>("currency", normalizedRequest.Currency));

            if (result.Kind == WalletResultKind.Replayed)
            {
                AppObservability.IdempotencyReplays.Add(
                    1,
                    new KeyValuePair<string, object?>("operation", "wallet.debit"));
            }
            else if (result.Kind == WalletResultKind.PayloadMismatch)
            {
                AppObservability.IdempotencyConflicts.Add(
                    1,
                    new KeyValuePair<string, object?>("operation", "wallet.debit"));
            }

            if (result.Kind == WalletResultKind.Created || result.Kind == WalletResultKind.Replayed)
            {
                _logger.LogInformation(
                    "Wallet debit returned result {DebitResult} for wallet {WalletId}, customer {CustomerId}, and reference {Reference}",
                    resultName,
                    result.Response!.WalletId,
                    normalizedCustomerId,
                    normalizedRequest.Reference);
            }
            else
            {
                _logger.LogWarning(
                    "Wallet debit returned result {DebitResult} for customer {CustomerId} and reference {Reference}",
                    resultName,
                    normalizedCustomerId,
                    normalizedRequest.Reference);
            }

            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            AppObservability.WalletDebitAttempts.Add(1, new KeyValuePair<string, object?>("result", "Error"));
            _logger.LogError(
                ex,
                "Wallet debit failed for customer {CustomerId} and reference {Reference}",
                normalizedCustomerId,
                normalizedRequest.Reference);
            throw;
        }
        finally
        {
            AppObservability.OperationDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("operation", "wallet.debit"));
        }
    }

    public Task<IReadOnlyList<OutboxMessageResponse>> GetPendingOutboxMessagesAsync(
        CancellationToken cancellationToken)
    {
        return _repository.GetPendingOutboxMessagesAsync(cancellationToken);
    }

    public Task<IReadOnlyList<OutboxMessageResponse>> GetDeadLetteredOutboxMessagesAsync(
        CancellationToken cancellationToken)
    {
        return _repository.GetDeadLetteredOutboxMessagesAsync(cancellationToken);
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
