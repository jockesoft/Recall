using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Postgres.Account;

/// <summary>
/// The wrong-code counter on a login token, against real PostgreSQL: one
/// atomic statement adds the attempt and, at the limit, consumes the token, so
/// guesses arriving together are all counted and the limit cannot be overshot.
/// </summary>
[TestFixture]
public sealed class SignInCodeTokenTests : PostgresFixture
{
    private async Task<Guid> SeedTokenAsync(Guid user, DateTime? createdUtc = null, string? codeHash = "hash")
    {
        var id = Guid.NewGuid();
        await using var db = NewContext();
        db.LoginTokens.Add(new LoginTokenEntity
        {
            Id = id, UserId = user, TokenHash = Guid.NewGuid().ToString("N"), CodeHash = codeHash,
            ExpiresUtc = DateTime.UtcNow.AddMinutes(15)
        });
        await db.SaveChangesAsync();
        if (createdUtc is { } created)
            await db.LoginTokens.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedUtc, created));
        return id;
    }

    [Test]
    public async Task RecordWrongCode_Should_CountEachAttempt_AndConsumeTheTokenAtTheLimit()
    {
        var token = await SeedTokenAsync(await SeedUserAsync());
        await using var db = NewContext();
        var repository = new LoginTokenRepository(db);

        var counts = new List<int>();
        for (var i = 0; i < 6; i++)
            counts.Add(await repository.RecordWrongCodeAsync(token, maxAttempts: 5));

        counts.Should().Equal([1, 2, 3, 4, 5, 0], "after the fifth the token is consumed, and a consumed token counts nothing more");
        var row = await NewContext().LoginTokens.SingleAsync(x => x.Id == token);
        row.ConsumedUtc.Should().NotBeNull();
        row.CodeAttempts.Should().Be(5);
    }

    [Test]
    public async Task RecordWrongCode_Should_CountGuessesArrivingTogether_WithoutOvershootingTheLimit()
    {
        var token = await SeedTokenAsync(await SeedUserAsync());

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var db = NewContext();
            await new LoginTokenRepository(db).RecordWrongCodeAsync(token, maxAttempts: 5);
        }));

        var row = await NewContext().LoginTokens.SingleAsync(x => x.Id == token);
        row.CodeAttempts.Should().Be(5, "no guess past the limit is counted, or answered as a fresh one");
        row.ConsumedUtc.Should().NotBeNull();
    }

    [Test]
    public async Task GetNewestActiveWithCode_Should_TakeTheUsersNewestUsableTokenThatHasACode()
    {
        var user = await SeedUserAsync();
        var other = await SeedUserAsync();
        await SeedTokenAsync(user, createdUtc: DateTime.UtcNow.AddMinutes(-10));
        var newest = await SeedTokenAsync(user, createdUtc: DateTime.UtcNow.AddMinutes(-1));
        await SeedTokenAsync(user, createdUtc: DateTime.UtcNow, codeHash: null);   // from before codes existed
        await SeedTokenAsync(other, createdUtc: DateTime.UtcNow);

        await using var db = NewContext();
        var found = await new LoginTokenRepository(db).GetNewestActiveWithCodeAsync(user, DateTime.UtcNow);

        found!.Id.Should().Be(newest);
    }
}
