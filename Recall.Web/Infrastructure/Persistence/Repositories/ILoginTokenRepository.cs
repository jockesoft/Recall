using Recall.Web.Domain.Internal;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public interface ILoginTokenRepository
{
    Task AddAsync(LoginToken token, CancellationToken cancellationToken = default);

    /// <summary>
    /// The token for <paramref name="tokenHash"/> if it exists, has not been
    /// consumed, and has not expired as of <paramref name="nowUtc"/>; otherwise
    /// <c>null</c>.
    /// </summary>
    Task<LoginToken?> GetActiveByHashAsync(
        string tokenHash,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a single token consumed (sets <c>consumed_utc</c>).</summary>
    Task MarkConsumedAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Consumes every still-usable token for a user in one round trip.</summary>
    Task InvalidateActiveForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
