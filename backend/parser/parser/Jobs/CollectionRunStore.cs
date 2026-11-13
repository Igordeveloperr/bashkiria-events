using Bashkiria.Events.Parser.Common;
using System.Text.Json;
using Bashkiria.Events.Parser.Collection;
using Bashkiria.Events.Parser.Data;
using Bashkiria.Events.Parser.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bashkiria.Events.Parser.Jobs;

public sealed class CollectionRunStore(IDbContextFactory<ParserDbContext> contexts, TimeProvider clock)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    public async Task<CollectionRun> EnqueueAsync(string sourceId, int maxPages, string? scheduleKey, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var active = await db.CollectionRuns.AsNoTracking().FirstOrDefaultAsync(run => run.SourceId == sourceId &&
            (run.Status == CollectionRunStatus.Queued || run.Status == CollectionRunStatus.Running), cancellationToken);
        if (active is not null) return active;
        var run = new CollectionRun
        {
            Id = Guid.NewGuid(),
            SourceId = sourceId,
            MaxPages = maxPages,
            Status = CollectionRunStatus.Queued,
            CreatedAt = clock.GetUtcNow(),
            ScheduleKey = scheduleKey
        };
        await db.CollectionRuns.AddAsync(run, cancellationToken);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "parser_runs_one_active_source" or "parser_runs_schedule_key" })
        {
            var conflict = (PostgresException)exception.InnerException!;
            var existing = db.CollectionRuns.AsNoTracking().Where(item => item.SourceId == sourceId);
            if (conflict.ConstraintName == "parser_runs_schedule_key") existing = existing.Where(item => item.ScheduleKey == scheduleKey);
            // The winning run may already have finished before the losing INSERT observes the conflict.
            return await existing.OrderByDescending(item => item.CreatedAt).FirstAsync(cancellationToken);
        }
        return run;
    }

    public async Task<CollectionRun?> ClaimNextAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var now = clock.GetUtcNow();
        await db.CollectionRuns.Where(run => run.Status == CollectionRunStatus.Running && run.LeaseExpiresAt < now && run.Attempts >= 3)
            .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.Status, CollectionRunStatus.Failed)
                .SetProperty(run => run.FinishedAt, now).SetProperty(run => run.LeaseOwner, (Guid?)null)
                .SetProperty(run => run.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(run => run.Error, "Превышено число восстановлений прерванного сбора."), cancellationToken);
        await db.CollectionRuns.Where(run => run.Status == CollectionRunStatus.Running && run.LeaseExpiresAt < now && run.Attempts < 3)
            .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.Status, CollectionRunStatus.Queued)
                .SetProperty(run => run.LeaseOwner, (Guid?)null).SetProperty(run => run.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken);
        var candidate = await db.CollectionRuns.AsNoTracking().Where(run => run.Status == CollectionRunStatus.Queued)
            .OrderBy(run => run.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (candidate is null) return null;
        var expires = now + LeaseDuration;
        var claimed = await db.CollectionRuns.Where(run => run.Id == candidate.Id && run.Status == CollectionRunStatus.Queued)
            .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.Status, CollectionRunStatus.Running)
                .SetProperty(run => run.StartedAt, now).SetProperty(run => run.LeaseOwner, owner)
                .SetProperty(run => run.LeaseExpiresAt, expires).SetProperty(run => run.Attempts, run => run.Attempts + 1), cancellationToken);
        return claimed == 1 ? candidate : null;
    }

    public async Task HeartbeatAsync(Guid id, Guid owner, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var expires = now + LeaseDuration;
        var count = await db.CollectionRuns.Where(run => run.Id == id && run.LeaseOwner == owner &&
                run.Status == CollectionRunStatus.Running && run.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.LeaseExpiresAt, expires), cancellationToken);
        if (count != 1) throw new RunLeaseLostException();
    }

    public async Task FinishAsync(Guid id, Guid owner, CollectionResult? result, string? error, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var status = error is not null ? CollectionRunStatus.Failed : result?.Errors.Count > 0
            ? CollectionRunStatus.CompletedWithErrors : CollectionRunStatus.Completed;
        var now = clock.GetUtcNow();
        var report = result is null ? null : ParserJson.Serialize(result);
        await db.CollectionRuns.Where(run => run.Id == id && run.LeaseOwner == owner && run.Status == CollectionRunStatus.Running)
            .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.Status, status).SetProperty(run => run.FinishedAt, now)
                .SetProperty(run => run.ReportJson, report).SetProperty(run => run.Error, error)
                .SetProperty(run => run.LeaseOwner, (Guid?)null).SetProperty(run => run.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken);
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var now = clock.GetUtcNow();
        return await db.CollectionRuns.Where(run => run.Id == id &&
                (run.Status == CollectionRunStatus.Queued || run.Status == CollectionRunStatus.Running))
            .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.Status, CollectionRunStatus.Cancelled)
                .SetProperty(run => run.FinishedAt, now).SetProperty(run => run.LeaseOwner, (Guid?)null)
                .SetProperty(run => run.LeaseExpiresAt, (DateTimeOffset?)null).SetProperty(run => run.Error, "Сбор остановлен администратором."), cancellationToken) == 1;
    }
}

public sealed class RunLeaseLostException : Exception;
