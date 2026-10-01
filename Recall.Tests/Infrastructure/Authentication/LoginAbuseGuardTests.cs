using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Recall.Web.Infrastructure.Authentication;

namespace Recall.Tests.Infrastructure.Authentication;

[TestFixture]
public class LoginAbuseGuardTests
{
    private static LoginAbuseGuard Create(int perEmailPerDay = 5, int siteWidePerHour = 100) =>
        new(Options.Create(new LoginTokenOptions
        {
            MaxRequestsPerEmailPerDay = perEmailPerDay,
            MaxSignInEmailsPerHourSiteWide = siteWidePerHour
        }));

    // ---- volumetric caps -----------------------------------------------------------

    [Test]
    public void TryAcquire_Should_AllowAnAddress_UpToItsDailyCap_ThenRefuse()
    {
        using var sut = Create(perEmailPerDay: 3);

        sut.TryAcquire("alice@test.local").Should().BeTrue();
        sut.TryAcquire("alice@test.local").Should().BeTrue();
        sut.TryAcquire("alice@test.local").Should().BeTrue();
        sut.TryAcquire("alice@test.local").Should().BeFalse();
    }

    [Test]
    public void TryAcquire_Should_CountEachAddressSeparately()
    {
        using var sut = Create(perEmailPerDay: 1);

        sut.TryAcquire("alice@test.local").Should().BeTrue();
        sut.TryAcquire("alice@test.local").Should().BeFalse();
        sut.TryAcquire("bob@test.local").Should().BeTrue("one address hitting its cap must not lock anyone else out");
    }

    [Test]
    public void TryAcquire_Should_RefuseEveryone_OnceTheSiteWideCapIsReached()
    {
        using var sut = Create(perEmailPerDay: 5, siteWidePerHour: 3);

        sut.TryAcquire("a@test.local").Should().BeTrue();
        sut.TryAcquire("b@test.local").Should().BeTrue();
        sut.TryAcquire("c@test.local").Should().BeTrue();

        sut.TryAcquire("d@test.local").Should().BeFalse("a fourth address, never seen before, is still over the site-wide cap");
    }

    [Test]
    public void TryAcquire_Should_TreatANonPositiveCap_AsOne()
    {
        using var sut = Create(perEmailPerDay: 0, siteWidePerHour: -4);

        sut.TryAcquire("alice@test.local").Should().BeTrue("a misconfigured 0 must not lock sign-in out completely");
        sut.TryAcquire("bob@test.local").Should().BeFalse();
    }

    // ---- resend cooldown -----------------------------------------------------------

    [Test]
    public void TryStartResendCooldown_Should_AllowTheFirstRequest_AndRefuseARepeatInsideTheCooldown()
    {
        using var sut = Create();
        var userId = Guid.NewGuid();

        sut.TryStartResendCooldown(userId, TimeSpan.FromMinutes(2)).Should().BeTrue();
        sut.TryStartResendCooldown(userId, TimeSpan.FromMinutes(2)).Should().BeFalse();
    }

    [Test]
    public void TryStartResendCooldown_Should_TrackEachUserSeparately()
    {
        using var sut = Create();

        sut.TryStartResendCooldown(Guid.NewGuid(), TimeSpan.FromMinutes(2)).Should().BeTrue();
        sut.TryStartResendCooldown(Guid.NewGuid(), TimeSpan.FromMinutes(2)).Should().BeTrue();
    }

    [Test]
    public async Task TryStartResendCooldown_Should_AllowAnotherRequest_OnceTheCooldownHasPassed()
    {
        using var sut = Create();
        var userId = Guid.NewGuid();
        var cooldown = TimeSpan.FromMilliseconds(40);

        sut.TryStartResendCooldown(userId, cooldown).Should().BeTrue();
        await Task.Delay(120);

        sut.TryStartResendCooldown(userId, cooldown).Should().BeTrue();
        sut.TryStartResendCooldown(userId, TimeSpan.FromMinutes(2)).Should().BeFalse("that second request started a new cooldown");
    }

    [Test]
    public void TryStartResendCooldown_Should_LetExactlyOneOfManySimultaneousRequestsThrough()
    {
        // The reason this is an atomic check-and-set rather than a database read:
        // two requests for the same user arriving together must not both send mail.
        using var sut = Create();
        var userId = Guid.NewGuid();
        var allowed = 0;

        Parallel.For(0, 200, _ =>
        {
            if (sut.TryStartResendCooldown(userId, TimeSpan.FromMinutes(2)))
                Interlocked.Increment(ref allowed);
        });

        allowed.Should().Be(1);
    }
}
