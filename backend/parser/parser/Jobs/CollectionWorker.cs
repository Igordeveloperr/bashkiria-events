using Bashkiria.Events.Parser.Collection;
using Bashkiria.Events.Parser.Configuration;
using Microsoft.Extensions.Options;

namespace Bashkiria.Events.Parser.Jobs;

public sealed class CollectionWorker(IServiceScopeFactory scopes, IOptions<ParserOptions> options,
    TimeProvider clock, ILogger<CollectionWorker> logger) : BackgroundService
{
    private readonly Guid _owner = Guid.NewGuid();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextSchedule = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var runs = scope.ServiceProvider.GetRequiredService<CollectionRunStore>();
                var now = clock.GetUtcNow();
                if (options.Value.SchedulerEnabled && now >= nextSchedule)
                {
                    var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();
                    var interval = TimeSpan.FromHours(options.Value.ScheduleIntervalHours);
                    var bucket = now.UtcTicks / interval.Ticks;
                    foreach (var source in registry.Sources.Where(source => source.Enabled))
                        await runs.EnqueueAsync(source.Id, options.Value.MaxPagesPerRun, $"{source.Id}:{bucket}", stoppingToken);
                    nextSchedule = now + interval;
                }
                var run = await runs.ClaimNextAsync(_owner, stoppingToken);
                if (run is null) { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); continue; }
                try
                {
                    var collector = scope.ServiceProvider.GetRequiredService<SourceCollector>();
                    var result = await collector.CollectAsync(run.SourceId, run.MaxPages,
                        token => runs.HeartbeatAsync(run.Id, _owner, token), stoppingToken);
                    await runs.FinishAsync(run.Id, _owner, result, null, stoppingToken);
                }
                catch (RunLeaseLostException) { logger.LogWarning("Lease lost for parser run {RunId}", run.Id); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Collection run {RunId} failed for source {SourceId}", run.Id, run.SourceId);
                    var message = exception switch
                    {
                        HttpRequestException { StatusCode: not null } http => $"Источник вернул HTTP {(int)http.StatusCode.Value}.",
                        HttpRequestException => "Не удалось установить соединение с источником.",
                        OperationCanceledException => "Источник не ответил за отведённое время.",
                        Storage.ParserStorageException => "Не удалось сохранить данные. Проверьте БД и миграции.",
                        _ => "Сбор не завершён; подробности в журнале сервиса."
                    };
                    await runs.FinishAsync(run.Id, _owner, null, message, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Parser worker could not process the queue");
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
