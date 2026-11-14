using Bashkiria.Events.Parser.Contracts;

namespace Bashkiria.Events.Parser.Ingestion;

public sealed class EventInputValidationException(IReadOnlyList<InputError> errors)
    : Exception("Проверьте поля мероприятия.")
{
    public IReadOnlyList<InputError> Errors { get; } = errors;
}

public sealed class IngestionRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
