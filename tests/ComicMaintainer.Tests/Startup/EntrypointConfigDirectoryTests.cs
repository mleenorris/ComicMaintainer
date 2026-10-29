namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// Text-level guards on the container entrypoint. The app keeps its database, DataProtection keys,
/// logs and settings in <c>CONFIG_DIR</c>, and runs as <c>PUID</c> after <c>gosu</c>, so a custom
/// <c>CONFIG_DIR</c> volume (often root-owned 0755) must be created and handed to <c>PUID</c> while
/// the script still runs as root.
/// </summary>
public class EntrypointConfigDirectoryTests
{
    private static string ReadEntrypoint()
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && current != null; i++)
        {
            var candidate = Path.Combine(current, "entrypoint.dotnet.sh");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            current = Path.GetDirectoryName(current);
        }
        throw new FileNotFoundException("Could not locate entrypoint.dotnet.sh relative to test binaries.");
    }

    [Fact]
    public void Entrypoint_PreparesCustomConfigDirForPuidBeforeDroppingPrivileges()
    {
        var script = ReadEntrypoint();
        var gosu = script.IndexOf("exec gosu", StringComparison.Ordinal);
        Assert.True(gosu > 0, "Entrypoint should drop privileges with 'exec gosu'.");

        var defaulted = script.IndexOf("CONFIG_DIR=\"${CONFIG_DIR:-/Config}\"", StringComparison.Ordinal);
        var created = script.IndexOf("mkdir -p \"$CONFIG_DIR\"", StringComparison.Ordinal);
        var owned = script.IndexOf("chown -R \"$PUID:$PGID\" \"$CONFIG_DIR\"", StringComparison.Ordinal);
        var ownerWritable = script.IndexOf("chmod -R u+rwX \"$CONFIG_DIR\"", StringComparison.Ordinal);

        Assert.InRange(defaulted, 0, gosu);
        Assert.InRange(created, defaulted, gosu);
        Assert.InRange(owned, created, gosu);
        Assert.InRange(ownerWritable, owned, gosu);
    }

    [Fact]
    public void Entrypoint_DoesNotWidenPermissionsOnConfigDirectories()
    {
        var script = ReadEntrypoint();

        // Config volumes hold credentials and DataProtection keys; only owner access may be added.
        Assert.DoesNotContain("chmod -R 755 \"$CONFIG_DIR\"", script);
        Assert.DoesNotContain("chmod -R 755 /Config", script);
    }
}
