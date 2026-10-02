using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Recall.Tests.Postgres.Queries;

/// <summary>
/// Records the SQL EF sends, with the name of its (single) parameter, so a
/// test can count the round trips and EXPLAIN the very statement that ran.
/// </summary>
internal sealed class CommandCapture : DbCommandInterceptor
{
    public List<(string Sql, string ParameterName)> Commands { get; } = [];

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        var name = command.Parameters.Count == 1 ? command.Parameters[0].ParameterName : string.Empty;
        Commands.Add((command.CommandText, name.StartsWith('@') ? name : "@" + name));
        return ValueTask.FromResult(result);
    }
}
