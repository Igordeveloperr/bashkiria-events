using Bashkiria.Events.Parser.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Bashkiria.Events.Parser.Api;

public static class ApiErrors
{
    public static IResult Problem(int status, string? title = null, IReadOnlyList<InputError>? errors = null) =>
        new ApiProblemResult(status, title ?? Title(status), errors ?? []);

    private sealed class ApiProblemResult(int status, string title, IReadOnlyList<InputError> errors) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            var problem = new ProblemDetails { Status = status, Title = title };
            problem.Extensions["errors"] = errors;
            // Enrich before content negotiation, including when Accept rejects the default writer.
            Enrich(context, problem);
            return Results.Problem(problem).ExecuteAsync(context);
        }
    }

    public static void Enrich(HttpContext context, ProblemDetails problem)
    {
        var status = problem.Status ?? context.Response.StatusCode;
        problem.Status = status;
        problem.Title ??= Title(status);
        problem.Instance = context.Request.Path.Value;
        problem.Extensions.TryAdd("code", Code(status));
        problem.Extensions["traceId"] = context.TraceIdentifier;
        problem.Extensions.TryAdd("errors", Array.Empty<InputError>());
        problem.Type = "urn:bashkiria-events:error:" + problem.Extensions["code"];
    }

    private static string Code(int status) => status switch
    {
        400 => "validation_error", 401 => "unauthorized", 403 => "forbidden", 404 => "not_found",
        405 => "method_not_allowed", 409 => "conflict", 413 => "payload_too_large", 415 => "unsupported_media_type",
        429 => "rate_limit_exceeded", 502 => "source_unavailable", 503 => "service_unavailable", 504 => "source_timeout",
        >= 500 => "internal_error", _ => "request_failed"
    };

    private static string Title(int status) => status switch
    {
        400 => "Некорректные параметры запроса.", 401 => "Требуется ключ доступа к API парсера.",
        403 => "Недостаточно прав для выполнения запроса.", 404 => "Запись или маршрут не найдены.",
        405 => "Метод HTTP не поддерживается для этого маршрута.", 409 => "Запрос конфликтует с текущим состоянием записи.",
        413 => "Превышен допустимый размер запроса.", 415 => "Неподдерживаемый тип содержимого запроса.",
        429 => "Превышен лимит запросов. Повторите запрос позже.", 502 => "Не удалось загрузить данные источника.",
        503 => "Сервис временно недоступен.", 504 => "Источник не ответил вовремя.",
        >= 500 => "Не удалось выполнить запрос.", _ => "Запрос не выполнен."
    };
}
