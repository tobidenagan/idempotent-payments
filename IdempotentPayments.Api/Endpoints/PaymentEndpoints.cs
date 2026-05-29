using IdempotentPayments.Api.Contracts;
using IdempotentPayments.Api.Domain;
using IdempotentPayments.Api.Services;

namespace IdempotentPayments.Api.Endpoints;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/payments", async (
            CreatePaymentRequest request,
            PaymentService service,
            CancellationToken cancellationToken) =>
        {
            var validationError = request.Validate();
            if (validationError is not null)
            {
                return Results.BadRequest(new ErrorResponse(validationError));
            }

            var result = await service.CreatePaymentAsync(request, cancellationToken);

            return result.Kind switch
            {
                PaymentResultKind.Created => Results.Created($"/payments/{result.Response!.PaymentId}", result.Response),
                PaymentResultKind.Replayed => Results.Json(result.Response, statusCode: StatusCodes.Status200OK),
                PaymentResultKind.PayloadMismatch => Results.Conflict(new ErrorResponse("Idempotency key was already used with a different request payload.")),
                _ => Results.Problem("Unexpected payment result.")
            };
        });

        return app;
    }
}
