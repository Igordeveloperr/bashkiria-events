using Bashkiria.Events.Interaction.Common;
using Bashkiria.Events.Interaction.Contracts;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bashkiria.Events.Interaction.Api;

public static class ApiErrors
{
    public static string Code(int status) => status switch
    {
        400 => "validation_error", 401 => "unauthorized", 403 => "forbidden", 404 => "not_found", 405 => "method_not_allowed",
        409 => "conflict", 413 => "payload_too_large", 415 => "unsupported_media_type", 429 => "rate_limit_exceeded",
        503 => "service_unavailable", _ => "internal_error"
    };
    public static IResult Result(int status, string? code = null, string? title = null, IReadOnlyList<InputError>? errors = null) =>
        new ErrorResult(status, code ?? Code(status), title ?? status switch
        {
            400 => "Некорректные параметры запроса.", 401 => "Требуется ключ доступа к сервису.",
            403 => "Недостаточно прав.", 404 => "Запись или маршрут не найдены.", 405 => "Метод HTTP не поддерживается.",
            409 => "Запрос конфликтует с текущим состоянием записи.", 413 => "Превышен допустимый размер запроса.",
            415 => "Неподдерживаемый тип содержимого.", 429 => "Слишком много запросов. Повторите позже.",
            503 => "Сервис временно недоступен.", _ => "Не удалось выполнить запрос."
        }, errors ?? []);

    private sealed class ErrorResult(int status, string code, string title, IReadOnlyList<InputError> errors) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = status;
            if (status == 429 && !context.Response.Headers.ContainsKey("Retry-After")) context.Response.Headers.RetryAfter = "3600";
            var problem = new ProblemDetails
            {
                Status = status, Type = "urn:bashkiria-events:error:" + code, Title = title, Instance = context.Request.Path,
                Extensions = { ["code"] = code, ["traceId"] = context.TraceIdentifier, ["errors"] = errors }
            };
            return Results.Problem(problem).ExecuteAsync(context);
        }
    }
}

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken token)
    {
        if (context.RequestAborted.IsCancellationRequested) return false;
        var error = exception switch
        {
            ServiceException service => service,
            BadHttpRequestException request => new ServiceException(request.StatusCode, ApiErrors.Code(request.StatusCode), "Некорректный формат запроса."),
            DbUpdateConcurrencyException => ServiceException.Conflict(),
            DbUpdateException { InnerException: PostgresException { SqlState: "23505" } } => ServiceException.Conflict(),
            DbUpdateException or NpgsqlException => new ServiceException(503, "service_unavailable", "База данных недоступна или схема не обновлена."),
            _ => new ServiceException(500, "internal_error", "Не удалось выполнить запрос.")
        };
        logger.Log(error.Status >= 500 ? LogLevel.Error : LogLevel.Warning,
            "Interaction request failed: {Code}, {Status}, {ErrorType}, trace {TraceId}", error.Code, error.Status, exception.GetType().Name, context.TraceIdentifier);
        await ApiErrors.Result(error.Status, error.Code, error.Message, error.Errors).ExecuteAsync(context);
        return true;
    }
}
