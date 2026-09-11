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

    /// <summary>
    /// Marks a single token consumed (sets <c>consumed_utc</c>), atomically, only
    /// if it wasn't already. Returns <c>true</c> when this call performed the
    /// consumption, <c>false</c> when the token was already consumed (e.g. by a
    /// concurrent redemption of the same link) or doesn't exist — callers must
    /// treat <c>false</c> as "this redemption did not win" rather than proceeding.
    /// </summary>
    Task<bool> MarkConsumedAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Consumes every still-usable token for a user in one round trip.</summary>
    Task InvalidateActiveForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
