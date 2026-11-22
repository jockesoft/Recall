namespace Recall.Web.Infrastructure.Hosting;

/// <summary>
/// Production's startup check of the Data Protection key folder: it must exist
/// and be writable by the user the app runs as, or the app does not start.
/// </summary>
/// <remarks>
/// <para>
/// The keys protect the sign-in cookie, antiforgery tokens, TempData and the
/// digest's unsubscribe links, which never expire. They are not configured
/// anywhere, so the framework keeps them in its default folder,
/// <c>$HOME/.aspnet/DataProtection-Keys</c> (<c>/home/nonroot/...</c> in the
/// hardened image), which compose.prod.yml bind-mounts from the server's
/// <c>./dataprotection-keys</c>. When that folder cannot be written, the
/// framework only logs and carries on with keys it cannot keep: every
/// deploy then signs everyone out and breaks every unsubscribe link already
/// sent. Stopping instead makes the deploy's <c>up --wait</c> fail, which is
/// the moment to fix the owner (UID <see cref="ExpectedOwnerUid"/>).
/// </para>
/// <para>
/// Writability is tested by creating and deleting a file, the only test that
/// covers ownership, mode, ACLs and read-only mounts alike. The folder and
/// where it is are left as they are: nothing here changes where keys live.
/// </para>
/// </remarks>
public static class DataProtectionKeyFolder
{
    /// <summary>The hardened runtime image's nonroot user, which the app runs as.</summary>
    public const int ExpectedOwnerUid = 65532;

    /// <summary>What was found: whether the folder exists, can be written, and how many key files it holds.</summary>
    public sealed record Status(string Path, bool Exists, bool Writable, int KeyCount, string? Problem);

    /// <summary>Looks at <paramref name="path"/> without changing anything but a probe file it removes again.</summary>
    public static Status Inspect(string path)
    {
        if (!Directory.Exists(path))
            return new Status(path, Exists: false, Writable: false, KeyCount: 0, Problem: "the folder does not exist");

        var keyCount = Directory.EnumerateFiles(path, "key-*.xml").Count();
        var probe = System.IO.Path.Combine(path, $".write-check-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return new Status(path, Exists: true, Writable: true, keyCount, Problem: null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new Status(path, Exists: true, Writable: false, keyCount, Problem: ex.Message);
        }
    }

    /// <summary>
    /// Checks the folder and logs the outcome: Information with the key count
    /// when it is usable, Critical (with what to do) when it is not.
    /// <paramref name="path"/> is the framework's own default folder
    /// (<c>FileSystemXmlRepository.DefaultKeyStorageDirectory</c>), null when
    /// it could not determine or create one. Returns whether the app may start.
    /// </summary>
    public static bool Verify(string? path, ILogger logger)
    {
        if (path is null)
        {
            logger.LogCritical(
                "Data Protection has no key folder (HOME is not set, or $HOME/.aspnet/DataProtection-Keys could not be created), " +
                "so keys would be kept in memory only and lost on restart. Not starting. Mount the key folder at " +
                "/home/nonroot/.aspnet/DataProtection-Keys, owned by UID {Uid}.",
                ExpectedOwnerUid);
            return false;
        }

        var status = Inspect(path);
        if (!status.Exists)
        {
            logger.LogCritical(
                "The Data Protection key folder {Folder} does not exist. Not starting: keys written anywhere else would be " +
                "lost on the next deploy. Mount ./dataprotection-keys there, owned by UID {Uid}.",
                status.Path, ExpectedOwnerUid);
            return false;
        }

        if (!status.Writable)
        {
            logger.LogCritical(
                "The Data Protection key folder {Folder} is not writable by the user the app runs as ({User}): {Problem}. " +
                "Not starting, rather than signing everyone out with keys that cannot be kept. The folder (on the server, " +
                "./dataprotection-keys) must be owned by UID {Uid}: sudo chown -R {Uid}:{Uid} dataprotection-keys",
                status.Path, Environment.UserName, status.Problem, ExpectedOwnerUid, ExpectedOwnerUid, ExpectedOwnerUid);
            return false;
        }

        logger.LogInformation(
            "Data Protection key folder {Folder} is writable and holds {KeyCount} key file(s).", status.Path, status.KeyCount);
        return true;
    }
}
