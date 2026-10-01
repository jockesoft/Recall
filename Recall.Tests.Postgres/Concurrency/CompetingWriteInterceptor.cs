using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Recall.Tests.Postgres.Concurrency;

/// <summary>
/// Plays the part of a second request that gets in first. The code under test
/// checks that a row is absent and then inserts it; this runs
/// <paramref name="competingWrite"/> between those two steps, just before the
/// first <c>SaveChanges</c>, so the insert hits the unique index on every run.
/// A race between two real tasks would only do that some of the time.
/// </summary>
public sealed class CompetingWriteInterceptor(Func<Task> competingWrite) : SaveChangesInterceptor
{
    private int _fired;

    /// <summary>True once the competing write has run; a test that never saved would leave it false.</summary>
    public bool Fired => Volatile.Read(ref _fired) == 1;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        // Only the first save: the code under test may save again to recover.
        if (Interlocked.Exchange(ref _fired, 1) == 0)
            await competingWrite();

        return result;
    }
}
