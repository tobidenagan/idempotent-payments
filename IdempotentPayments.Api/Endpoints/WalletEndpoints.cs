using IdempotentPayments.Api.Contracts;
using IdempotentPayments.Api.Domain;
using IdempotentPayments.Api.Services;

namespace IdempotentPayments.Api.Endpoints;

public static class WalletEndpoints
{
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/wallets/{customerId}/credits", async (
            string customerId,
            CreditWalletRequest request,
            WalletService service,
            CancellationToken cancellationToken) =>
        {
            var validationError = request.Validate();
            if (validationError is not null)
            {
                return Results.BadRequest(new ErrorResponse(validationError));
            }

            var response = await service.CreditWalletAsync(customerId, request, cancellationToken);
            return Results.Ok(response);
        });

        app.MapPost("/wallets/{customerId}/debits", async (
            string customerId,
            DebitWalletRequest request,
            WalletService service,
            CancellationToken cancellationToken) =>
        {
            var validationError = request.Validate();
            if (validationError is not null)
            {
                return Results.BadRequest(new ErrorResponse(validationError));
            }

            var result = await service.DebitWalletAsync(customerId, request, cancellationToken);

            return result.Kind switch
            {
                WalletResultKind.Created => Results.Created($"/wallets/{customerId}/ledger/{result.Response!.LedgerEntryId}", result.Response),
                WalletResultKind.Replayed => Results.Json(result.Response, statusCode: StatusCodes.Status200OK),
                WalletResultKind.PayloadMismatch => Results.Conflict(new ErrorResponse("Idempotency key was already used with a different wallet debit payload.")),
                WalletResultKind.InsufficientFunds => Results.UnprocessableEntity(new ErrorResponse("Insufficient funds.")),
                _ => Results.Problem("Unexpected wallet result.")
            };
        });

        app.MapGet("/outbox/pending", async (
            WalletService service,
            CancellationToken cancellationToken) =>
        {
            var messages = await service.GetPendingOutboxMessagesAsync(cancellationToken);
            return Results.Ok(messages);
        });

        app.MapGet("/outbox/dead-lettered", async (
            WalletService service,
            CancellationToken cancellationToken) =>
        {
            var messages = await service.GetDeadLetteredOutboxMessagesAsync(cancellationToken);
            return Results.Ok(messages);
        });

        return app;
    }
}
