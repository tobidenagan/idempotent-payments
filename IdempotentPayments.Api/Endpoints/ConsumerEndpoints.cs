using IdempotentPayments.Api.Contracts;
using IdempotentPayments.Api.Domain;
using IdempotentPayments.Api.Services;

namespace IdempotentPayments.Api.Endpoints;

public static class ConsumerEndpoints
{
    public static IEndpointRouteBuilder MapConsumerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/consumers/{consumerName}/events", async (
            string consumerName,
            ConsumeEventRequest request,
            ConsumerService service,
            CancellationToken cancellationToken) =>
        {
            var validationError = request.Validate();
            if (validationError is not null)
            {
                return Results.BadRequest(new ErrorResponse(validationError));
            }

            var result = await service.ConsumeEventAsync(consumerName, request, cancellationToken);

            return result.Kind switch
            {
                ConsumerResultKind.Processed => Results.Created($"/consumers/{consumerName}/events/{request.EventId}", result.Response),
                ConsumerResultKind.Duplicate => Results.Ok(result.Response),
                _ => Results.Problem("Unexpected consumer result.")
            };
        });

        return app;
    }
}
