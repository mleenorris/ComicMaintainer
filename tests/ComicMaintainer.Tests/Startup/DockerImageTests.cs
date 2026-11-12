using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// Checks the files the Docker image is built from. The image compiled the app twice, carried archive tools the app
/// never runs, and its build context took in local bin/ and obj/ folders, which replaced the restore done in the image
/// and changed the cached source layer on every local build.
/// </summary>
public sealed class DockerImageTests
{
    private static readonly string Dockerfile = File.ReadAllText(Path.Join(RepositoryRoot, "Dockerfile.dotnet")).Replace("\r\n", "\n");

    [Fact]
    public void Dockerfile_CompilesTheAppOnceWithPublish()
    {
        Assert.DoesNotMatch(@"(?m)^RUN dotnet build\b", Dockerfile);
        var publish = Assert.Single(Regex.Matches(Dockerfile, @"(?m)^RUN dotnet publish .*$")).Value;
        // The restore layers above are reused, so publish must not restore again.
        Assert.Contains("--no-restore", publish);
    }

    [Fact]
    public void Dockerfile_InstallsOnlyThePackagesTheContainerUses()
    {
        var install = Regex.Match(Dockerfile, @"apt-get install([^&]*)&&", RegexOptions.Singleline);

        Assert.True(install.Success, "Dockerfile.dotnet installs no packages with apt-get");
        Assert.Contains("--no-install-recommends", install.Groups[1].Value);
        var packages = Regex.Matches(install.Groups[1].Value, @"(?<=\s)[a-z0-9][a-z0-9.+-]+(?=\s)").Select(match => match.Value).ToHashSet();
        // gosu drops root in the entrypoint and curl runs the health check. Archives are read in-process.
        Assert.Equal(["curl", "gosu"], packages.Order());
    }

    [Fact]
    public void App_StartsNoExternalProgramsSuchAsArchiveTools()
    {
        // Dockerfile_InstallsOnlyThePackagesTheContainerUses relies on this: no unrar or 7-Zip is installed.
        var sources = Directory.EnumerateFiles(Path.Join(RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}ComicMaintainer.MauiApp{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        Assert.All(sources, file => Assert.DoesNotMatch(@"\bProcess\.Start\b|\bProcessStartInfo\b", File.ReadAllText(file)));
    }

    [Theory]
    [InlineData("**/bin/")]
    [InlineData("**/obj/")]
    [InlineData("tests/")]
    [InlineData("node_modules/")]
    [InlineData("src/ComicMaintainer.MauiApp/")]
    // Secrets are matched at any depth: a key or .env under src/ would be copied into the build stage by COPY src/ src/.
    [InlineData("**/.env")]
    [InlineData("**/.env.*")]
    [InlineData("**/*.pfx")]
    [InlineData("**/*.pem")]
    [InlineData("**/*.key")]
    public void DockerIgnore_KeepsLocalOutputAndUnusedFoldersOutOfTheBuildContext(string pattern)
    {
        var patterns = File.ReadAllLines(Path.Join(RepositoryRoot, ".dockerignore")).Select(line => line.Trim());

        Assert.Contains(pattern, patterns);
    }

    [Fact]
    public void ShellScripts_AreCheckedOutWithLfLineEndings()
    {
        // A CRLF entrypoint copied into an image built from a Windows checkout fails with "bad interpreter".
        var attributes = File.ReadAllLines(Path.Join(RepositoryRoot, ".gitattributes")).Select(line => Regex.Replace(line.Trim(), @"\s+", " "));

        Assert.Contains("*.sh text eol=lf", attributes);
    }

    private static string RepositoryRoot
    {
        get
        {
            var current = AppContext.BaseDirectory;
            for (var i = 0; i < 10 && current != null; i++)
            {
                if (File.Exists(Path.Join(current, "Dockerfile.dotnet")))
                {
                    return current;
                }
                current = Path.GetDirectoryName(current);
            }
            throw new DirectoryNotFoundException("Could not locate the repository root relative to test binaries.");
        }
    }
}
