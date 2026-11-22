using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.Hosting;

namespace Recall.Tests.Infrastructure.Hosting;

/// <summary>
/// Production's startup check of the Data Protection key folder: a writable
/// folder starts the app and logs its key count; a missing or read-only one
/// stops it with a Critical line that says what to fix.
/// </summary>
[TestFixture]
public sealed class DataProtectionKeyFolderTests
{
    private string _folder = null!;

    [SetUp]
    public void CreateFolder()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"recall-dp-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void RemoveFolder()
    {
        if (!Directory.Exists(_folder)) return;
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(_folder, recursive: true);
    }

    [Test]
    public void AWritableFolder_Should_LetTheAppStart_AndLogHowManyKeysItHolds()
    {
        File.WriteAllText(Path.Combine(_folder, "key-1.xml"), "<key/>");
        File.WriteAllText(Path.Combine(_folder, "key-2.xml"), "<key/>");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not a key");
        var logger = new RecordingLogger<DataProtectionKeyFolderTests>();

        DataProtectionKeyFolder.Verify(_folder, logger).Should().BeTrue();

        logger.Entries.Should().ContainSingle()
            .Which.Should().Match<(LogLevel Level, string Message)>(e =>
                e.Level == LogLevel.Information && e.Message.Contains("holds 2 key file(s)") && e.Message.Contains(_folder));
        Directory.EnumerateFileSystemEntries(_folder).Should().HaveCount(3, "the write check leaves nothing behind");
    }

    [Test]
    public void AnEmptyWritableFolder_Should_StartWithZeroKeys()
    {
        var logger = new RecordingLogger<DataProtectionKeyFolderTests>();

        DataProtectionKeyFolder.Verify(_folder, logger).Should().BeTrue();

        logger.Entries.Should().ContainSingle().Which.Message.Should().Contain("holds 0 key file(s)");
    }

    [Test]
    public void AFolderThatCannotBeWritten_Should_StopTheApp_WithACriticalLineNamingTheOwner()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("Unix file modes only.");
        File.WriteAllText(Path.Combine(_folder, "key-1.xml"), "<key/>");
        File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);   // r-x: as a folder owned by another user
        if (DataProtectionKeyFolder.Inspect(_folder).Writable)
            Assert.Ignore("Running as root: a read-only mode does not stop root from writing.");
        var logger = new RecordingLogger<DataProtectionKeyFolderTests>();

        DataProtectionKeyFolder.Verify(_folder, logger).Should().BeFalse();

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Critical);
        entry.Message.Should().Contain(_folder).And.Contain("not writable").And.Contain("65532").And.Contain("Not starting");
        entry.Message.Should().NotContain("..", "the exception's own full stop is not doubled");
    }

    [Test]
    public void AMissingFolder_Should_StopTheApp()
    {
        var missing = Path.Combine(_folder, "not-there");
        var logger = new RecordingLogger<DataProtectionKeyFolderTests>();

        DataProtectionKeyFolder.Verify(missing, logger).Should().BeFalse();

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Critical);
        entry.Message.Should().Contain(missing).And.Contain("does not exist").And.Contain("65532");
        Directory.Exists(missing).Should().BeFalse("the check never creates the folder");
    }

    [Test]
    public void NoFolderAtAll_Should_StopTheApp()
    {
        var logger = new RecordingLogger<DataProtectionKeyFolderTests>();

        DataProtectionKeyFolder.Verify(null, logger).Should().BeFalse();

        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Critical);
    }

    [Test]
    public void Inspect_Should_CountOnlyKeyFiles()
    {
        File.WriteAllText(Path.Combine(_folder, "key-a.xml"), "<key/>");
        File.WriteAllText(Path.Combine(_folder, "other.xml"), "<x/>");

        DataProtectionKeyFolder.Inspect(_folder).Should().Be(
            new DataProtectionKeyFolder.Status(_folder, Exists: true, Writable: true, KeyCount: 1, Problem: null));
    }
}
