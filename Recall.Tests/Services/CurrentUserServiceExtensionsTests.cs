using AwesomeAssertions;
using Moq;
using Recall.Web.Services;

namespace Recall.Tests.Services;

[TestFixture]
public class CurrentUserServiceExtensionsTests
{
    [Test]
    public void TryGetUserId_Should_ReturnTheId_ForASignedInUser()
    {
        var id = Guid.NewGuid();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        currentUser.SetupGet(x => x.UserId).Returns(id);

        currentUser.Object.TryGetUserId(out var userId).Should().BeTrue();
        userId.Should().Be(id);
    }

    [Test]
    public void TryGetUserId_Should_BeFalse_ForAnAnonymousRequest()
    {
        var currentUser = new Mock<ICurrentUserService>();

        currentUser.Object.TryGetUserId(out var userId).Should().BeFalse();
        userId.Should().Be(Guid.Empty);
    }

    [Test]
    public void TryGetUserId_Should_BeFalse_WhenAuthenticatedButTheIdIsUnusable()
    {
        // e.g. a principal whose NameIdentifier isn't a Guid.
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        currentUser.SetupGet(x => x.UserId).Returns((Guid?)null);

        currentUser.Object.TryGetUserId(out _).Should().BeFalse();
    }

    [Test]
    public void TryGetUserId_Should_BeFalse_WhenAnIdIsPresentButTheRequestIsNotAuthenticated()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.IsAuthenticated).Returns(false);
        currentUser.SetupGet(x => x.UserId).Returns(Guid.NewGuid());

        currentUser.Object.TryGetUserId(out _).Should().BeFalse();
    }
}
