using Bashkiria.Events.Interaction.Contracts;

namespace Bashkiria.Events.Interaction.Common;

public sealed class ServiceException(int status, string code, string message, IReadOnlyList<InputError>? errors = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IReadOnlyList<InputError> Errors { get; } = errors ?? [];
    public static ServiceException Invalid(string field, string message) => new(400, "validation_error", "Проверьте поля запроса.", [new(field, "invalid_value", message)]);
    public static ServiceException Missing() => new(404, "not_found", "Запись не найдена.");
    public static ServiceException Conflict() => new(409, "conflict", "Запись уже изменена или такой запрос уже существует.");
}
