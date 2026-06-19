namespace IdempotentPayments.Api.Contracts;

public sealed record ConsumeEventRequest(
    string EventId,
    string Type,
    string Payload)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(EventId))
        {
            return "EventId is required.";
        }

        if (string.IsNullOrWhiteSpace(Type))
        {
            return "Type is required.";
        }

        if (string.IsNullOrWhiteSpace(Payload))
        {
            return "Payload is required.";
        }

        return null;
    }
}
