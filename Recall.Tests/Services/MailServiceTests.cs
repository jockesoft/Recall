using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Web.Domain.Internal;
using Recall.Web.Infrastructure.Mail;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;

namespace Recall.Tests.Services;

[TestFixture]
public sealed class MailServiceTests
{
    private Mock<IEmailRepository> _repository = null!;
    private MailOptions _options = null!;
    private string _pickupDirectory = null!;
    private MailService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IEmailRepository>();

        _pickupDirectory = Path.Combine(Path.GetTempPath(), "recall-mail-tests", Guid.NewGuid().ToString("N"));

        _options = new MailOptions
        {
            FromAddress = "no-reply@test.local",
            FromDisplayName = "Recall Test",
            PickupDirectory = _pickupDirectory,
            BatchSize = 5,
            MaxSendAttempts = 3
        };

        _sut = new MailService(
            _repository.Object,
            Options.Create(_options),
            new StubHostEnvironment("Development"),
            NullLogger<MailService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_pickupDirectory))
            Directory.Delete(_pickupDirectory, recursive: true);
    }

    // ---- QueueEmailAsync ------------------------------------------------------

    [Test]
    public async Task QueueEmailAsync_Should_PersistPendingMessage_WithGivenFields()
    {
        OutboundEmail? captured = null;
        _repository
            .Setup(x => x.AddAsync(It.IsAny<OutboundEmail>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundEmail, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);

        await _sut.QueueEmailAsync("user@test.local", "Hello", "Body text", priority: 7);

        captured.Should().NotBeNull();
        captured!.Id.Should().NotBe(Guid.Empty);
        captured.ToAddress.Should().Be("user@test.local");
        captured.Subject.Should().Be("Hello");
        captured.Body.Should().Be("Body text");
        captured.Priority.Should().Be(7);
        captured.SendAttempts.Should().Be(0);
        captured.SentUtc.Should().BeNull();
    }

    [Test]
    public async Task QueueEmailAsync_Should_DefaultToNormalPriority()
    {
        OutboundEmail? captured = null;
        _repository
            .Setup(x => x.AddAsync(It.IsAny<OutboundEmail>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundEmail, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);

        await _sut.QueueEmailAsync("user@test.local", "Hello", "Body");

        captured!.Priority.Should().Be(MailService.NormalPriority);
    }

    [Test]
    public async Task QueueEmailAsync_Should_TreatNullBodyAsEmpty()
    {
        OutboundEmail? captured = null;
        _repository
            .Setup(x => x.AddAsync(It.IsAny<OutboundEmail>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundEmail, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);

        await _sut.QueueEmailAsync("user@test.local", "Hello", null!);

        captured!.Body.Should().BeEmpty();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task QueueEmailAsync_Should_Reject_BlankRecipient(string? to)
    {
        var act = () => _sut.QueueEmailAsync(to!, "Subject", "Body");

        await act.Should().ThrowAsync<ArgumentException>();
        _repository.Verify(x => x.AddAsync(It.IsAny<OutboundEmail>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task QueueEmailAsync_Should_Reject_BlankSubject(string? subject)
    {
        var act = () => _sut.QueueEmailAsync("user@test.local", subject!, "Body");

        await act.Should().ThrowAsync<ArgumentException>();
        _repository.Verify(x => x.AddAsync(It.IsAny<OutboundEmail>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- SendPendingEmailsAsync --------------------------------------------

    [Test]
    public async Task SendPendingEmailsAsync_Should_DoNothing_WhenQueueEmpty()
    {
        _repository
            .Setup(x => x.GetPendingAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _sut.SendPendingEmailsAsync();

        _repository.Verify(x => x.MarkSentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(x => x.RecordFailedAttemptAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task SendPendingEmailsAsync_Should_QueryUsing_ConfiguredBatchAndAttemptLimits()
    {
        _repository
            .Setup(x => x.GetPendingAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _sut.SendPendingEmailsAsync();

        _repository.Verify(
            x => x.GetPendingAsync(_options.BatchSize, _options.MaxSendAttempts, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task SendPendingEmailsAsync_Should_MarkSent_AndWritePickupFile_ForEachMessage()
    {
        var pending = new[]
        {
            Pending("a@test.local", "First"),
            Pending("b@test.local", "Second")
        };
        _repository
            .Setup(x => x.GetPendingAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending);

        await _sut.SendPendingEmailsAsync();

        foreach (var email in pending)
            _repository.Verify(x => x.MarkSentAsync(email.Id, It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.RecordFailedAttemptAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        Directory.GetFiles(_pickupDirectory, "*.eml").Should().HaveCount(2);
    }

    [Test]
    public async Task SendPendingEmailsAsync_Should_RecordFailedAttempt_ForBadMessage_AndKeepGoing()
    {
        var good = Pending("good@test.local", "Fine");
        var bad = Pending("not a valid address", "Broken");
        _repository
            .Setup(x => x.GetPendingAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([bad, good]);

        await _sut.SendPendingEmailsAsync();

        _repository.Verify(x => x.MarkSentAsync(good.Id, It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.MarkSentAsync(bad.Id, It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(x => x.RecordFailedAttemptAsync(bad.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static OutboundEmail Pending(string to, string subject) => new()
    {
        Id = Guid.NewGuid(),
        ToAddress = to,
        Subject = subject,
        Body = "body",
        SentUtc = null,
        SendAttempts = 0,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Recall.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
