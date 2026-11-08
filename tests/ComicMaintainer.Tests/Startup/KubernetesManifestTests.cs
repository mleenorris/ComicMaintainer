using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// Checks the example Kubernetes manifest in docs/kubernetes-deployment.yaml. It used to run the Python-era image, and
/// with the default RollingUpdate strategy every upgrade briefly ran two pods on one SQLite database and one library.
/// </summary>
public sealed class KubernetesManifestTests
{
    private static readonly string Manifest = File.ReadAllText(Path.Join(RepositoryRoot, "docs", "kubernetes-deployment.yaml"))
        .Replace("\r\n", "\n");

    [Fact]
    public void Deployment_RunsTheDotnetImage()
    {
        var images = Regex.Matches(Manifest, @"^\s*image:\s*(\S+)", RegexOptions.Multiline).Select(match => match.Groups[1].Value).ToList();

        var image = Assert.Single(images);
        Assert.StartsWith("iceburn1/comicmaintainer:", image);
        Assert.DoesNotContain("comictagger-watcher", Manifest);
    }

    [Fact]
    public void Deployment_RunsOnePodAtATimeAndStopsTheOldOneBeforeStartingTheNext()
    {
        var deployment = Document("Deployment");

        Assert.Matches(@"(?m)^  replicas: 1\s*(#.*)?$", deployment);
        Assert.Matches(@"(?m)^  strategy:\n    type: Recreate$", deployment);
        Assert.DoesNotContain("kind: HorizontalPodAutoscaler", Manifest);
    }

    [Fact]
    public void Deployment_ReadinessChecksTheDatabase()
    {
        var deployment = Document("Deployment");

        var readiness = Regex.Match(deployment, @"readinessProbe:\n\s+httpGet:\n\s+path: (\S+)");
        Assert.True(readiness.Success, "The Deployment has no HTTP readiness probe");
        Assert.Equal("/health/ready", readiness.Groups[1].Value);
    }

    [Fact]
    public void Deployment_MountsEveryFolderTheConfigMapNamesAndEveryMountHasAVolume()
    {
        var deployment = Document("Deployment");
        var configMap = Document("ConfigMap");

        var mounts = Regex.Matches(deployment, @"- name: (\S+)\n\s+mountPath: (\S+)").ToDictionary(match => match.Groups[2].Value, match => match.Groups[1].Value);
        var volumes = Regex.Matches(deployment, @"- name: (\S+)\n\s+(?:persistentVolumeClaim|secret|emptyDir|hostPath|nfs):").Select(match => match.Groups[1].Value).ToHashSet();
        Assert.All(mounts.Values, volume => Assert.Contains(volume, volumes));

        foreach (var variable in new[] { "WATCHED_DIR", "DUPLICATE_DIR" })
        {
            var folder = Regex.Match(configMap, $@"(?m)^  {variable}: ""([^""]+)""").Groups[1].Value;
            Assert.True(mounts.ContainsKey(folder), $"{variable} is {folder}, which is not mounted");
        }

        Assert.Contains("/Config", mounts.Keys);
    }

    [Fact]
    public void Secrets_AreReadFromAMountedSecretNotWrittenInTheManifest()
    {
        var configMap = Document("ConfigMap");
        var deployment = Document("Deployment");

        Assert.DoesNotMatch(@"(?m)^\s*(JWT_SECRET|ADMIN_PASSWORD):", Manifest);
        Assert.DoesNotContain("kind: Secret", Manifest);
        var secretMount = Regex.Match(deployment, @"- name: secrets\n\s+mountPath: (\S+)").Groups[1].Value;
        Assert.NotEmpty(secretMount);
        foreach (var variable in new[] { "JWT_SECRET_FILE", "ADMIN_PASSWORD_FILE" })
        {
            Assert.Matches($@"(?m)^  {variable}: ""{Regex.Escape(secretMount)}/[^""]+""", configMap);
        }
    }

    /// <summary>The YAML document in the manifest that declares <paramref name="kind"/>.</summary>
    private static string Document(string kind) =>
        Assert.Single(Manifest.Split("\n---\n"), document => Regex.IsMatch(document, $@"(?m)^kind: {kind}$"));

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
