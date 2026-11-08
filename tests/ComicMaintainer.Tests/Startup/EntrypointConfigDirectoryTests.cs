using System.Text.RegularExpressions;

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
            var candidate = Path.Join(current, "entrypoint.dotnet.sh");
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

    [Fact]
    public void Entrypoint_ChangesTheLibraryOnlyThroughTheOptInFindThatSkipsSecretFiles()
    {
        var script = ReadEntrypoint();
        var gosu = script.IndexOf("exec gosu", StringComparison.Ordinal);

        // A recursive chmod cannot skip the JWT signing key or the files named by JWT_SECRET_FILE and
        // ADMIN_PASSWORD_FILE, so a recursive chmod may only add owner access, and only to the config directories.
        Assert.All(Regex.Matches(script, @"\bchmod\s+-R\s+(\S+)"), chmod => Assert.Equal("u+rwX", chmod.Groups[1].Value));

        // The library is changed in one place: the find that FIX_LIBRARY_PERMISSIONS opts in to, after the JWT signing
        // key is made private. It only adds owner and group access, never a fixed mode.
        var privatized = script.IndexOf("-exec chmod 600 {} +", StringComparison.Ordinal);
        var optIn = script.IndexOf("if [ \"$FIX_LIBRARY_PERMISSIONS\" = true ]; then", StringComparison.Ordinal);
        var widened = script.IndexOf(
            "\"${SECRET_FILE_EXCLUSIONS[@]}\" -exec chown \"$PUID:$PGID\" {} + -exec chmod u+rwX,g+rwX {} +", StringComparison.Ordinal);
        Assert.InRange(privatized, 0, gosu);
        Assert.InRange(optIn, privatized, gosu);
        Assert.InRange(widened, optIn, gosu);
        Assert.Single(Regex.Matches(script, @"-exec chmod u\+rwX,g\+rwX"));
        Assert.DoesNotMatch(@"\bchmod\s+(-R\s+)?755\b", script);
    }

    [Fact]
    public void Entrypoint_LeavesConfigDirectoriesOutOfTheLibraryPermissionChange()
    {
        var script = ReadEntrypoint();

        // A config directory may be inside the library or the duplicates folder, so the find that repairs them prunes both
        // /Config and CONFIG_DIR; otherwise the database and DataProtection keys would become group-writable.
        var listed = script.IndexOf("for config_dir in /Config \"$CONFIG_DIR\"; do", StringComparison.Ordinal);
        var excluded = script.IndexOf("CONFIG_DIR_EXCLUSIONS+=(-samefile \"$config_dir\" -prune -o)", StringComparison.Ordinal);
        var widening = script.IndexOf("find \"$library_dir\" \"${CONFIG_DIR_EXCLUSIONS[@]}\" ", StringComparison.Ordinal);
        var widened = script.IndexOf("-exec chmod u+rwX,g+rwX {} +", StringComparison.Ordinal);
        Assert.InRange(listed, 0, excluded);
        Assert.InRange(excluded, listed, widening);
        Assert.InRange(widening, excluded, widened);
        Assert.Single(Regex.Matches(script[widening..widened], @"\bfind\b"));
    }
}
