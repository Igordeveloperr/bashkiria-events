using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bashkiria.Events.Parser.Data;

public static class DatabaseErrors
{
    public static bool IsConflict(Exception exception) => Chain(exception).Any(error =>
        error is DbUpdateConcurrencyException || error is PostgresException { SqlState: "23505" or "40001" or "40P01" });

    public static bool IsDatabaseFailure(Exception exception) =>
        Chain(exception).Any(error => error is DbException or DbUpdateException);

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException) yield return current;
    }
}
