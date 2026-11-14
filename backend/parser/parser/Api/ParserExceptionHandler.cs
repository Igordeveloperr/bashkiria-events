using Bashkiria.Events.Parser.Jobs;
using Bashkiria.Events.Parser.Storage;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Bashkiria.Events.Parser.Common;
using Bashkiria.Events.Parser.Ingestion;

namespace Bashkiria.Events.Parser.Api;

public sealed class ParserExceptionHandler(ILogger<ParserExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested) return false;
        var (status, title) = exception switch
        {
            BadHttpRequestException badRequest => (badRequest.StatusCode, "Некорректный формат запроса."),
            IngestionRequestException input => (input.StatusCode, input.Message),
            EventInputValidationException input => (400, input.Message),
            DbUpdateConcurrencyException or RunLeaseLostException => (409, "Запись уже изменена. Обновите данные и повторите запрос."),
            DbUpdateException { InnerException: PostgresException { SqlState: "23505" } } => (409, "Такая запись уже существует."),
            ParserStorageException or NpgsqlException or DbUpdateException => (503, "База данных недоступна или схема не обновлена."),
            HttpRequestException => (502, "Не удалось загрузить страницу источника."),
            OperationCanceledException => (504, "Источник не ответил вовремя."),
            KeyNotFoundException => (404, "Запись или источник не найдены."),
            ParserValidationException validation => (400, validation.Message),
            ArgumentException => (400, "Некорректные параметры запроса."),
            _ => (500, "Не удалось выполнить запрос.")
        };
        logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning, exception,
            "Parser request failed with status {Status}, trace {TraceId}", status, context.TraceIdentifier);
        await ApiErrors.Problem(status, title, (exception as EventInputValidationException)?.Errors).ExecuteAsync(context);
        return true;
    }
}
