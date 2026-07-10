namespace Recall.Web.Infrastructure.Persistence.Entities;

public sealed class AppUserEntity
{
    public Guid Id { get; set; }

    // If/when you add ASP.NET Core Identity, store IdentityUser.Id here.
    public string ExternalId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public ICollection<TrackedSeriesEntity> TrackedSeries { get; set; } = new List<TrackedSeriesEntity>();
}