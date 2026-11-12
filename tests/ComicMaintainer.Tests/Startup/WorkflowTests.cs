using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// Checks the GitHub Actions workflows, which cannot run outside GitHub: that every one parses, and
/// that the release pipeline keeps the guarantees it was built for. Images and releases used to be
/// published without waiting for the tests, the Windows and Android release builds never ran because
/// release tags pushed with GITHUB_TOKEN start no workflow, and the security scan only knew the
/// Python version.
/// </summary>
public sealed class WorkflowTests
{
    private const string Release = "version-bump.yml";
    private const string Tests = "test-dotnet.yml";
    private const string ContainerTests = "integration-tests-e2e.yml";
    private const string DockerPublish = "docker-publish-dotnet.yml";
    private const string SecurityScan = "security-scan.yml";
    private const string PrTestCheck = "pr-test-check.yml";

    public static TheoryData<string> WorkflowFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(WorkflowDirectory, "*.yml").Select(Path.GetFileName).Order(StringComparer.Ordinal))
        {
            data.Add(file!);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(WorkflowFiles))]
    public void Workflow_ParsesWithTriggersAndJobs(string file)
    {
        var workflow = Load(file);

        Assert.True(workflow.Children.ContainsKey(new YamlScalarNode("on")), $"{file} has no triggers");
        var jobs = Mapping(workflow, "jobs");
        Assert.NotEmpty(jobs.Children);
        foreach (var (name, job) in jobs.Children)
        {
            var definition = Assert.IsType<YamlMappingNode>(job);
            Assert.True(
                definition.Children.ContainsKey(new YamlScalarNode("runs-on")) || definition.Children.ContainsKey(new YamlScalarNode("uses")),
                $"{file}: job {name} has neither runs-on nor uses");
        }
    }

    [Theory]
    [MemberData(nameof(WorkflowFiles))]
    public void Workflow_PassesOutsideValuesToScriptsThroughTheEnvironment(string file)
    {
        // Expanding inputs, branch names or pull request text into a run script lets them run as shell code.
        foreach (var (job, step) in Steps(Load(file)))
        {
            if (Scalar(step, "run") is { } script)
            {
                Assert.DoesNotMatch(@"\$\{\{\s*(inputs\.|github\.head_ref|github\.event\.(pull_request|issue|comment|head_commit))", script);
            }
        }
    }

    [Fact]
    public void Release_PublishesOnlyAfterTheTestsOfThePushedCommitPass()
    {
        var release = Load(Release);
        Assert.Equal(["push"], Triggers(release));
        Assert.Equal(["dotnet"], Sequence(Mapping(Mapping(release, "on"), "push"), "branches"));

        var jobs = Mapping(release, "jobs");
        Assert.Equal($"./.github/workflows/{Tests}", Scalar(Mapping(jobs, "tests"), "uses"));
        Assert.Equal($"./.github/workflows/{ContainerTests}", Scalar(Mapping(jobs, "container-tests"), "uses"));
        Assert.Equal(["tests", "container-tests"], Sequence(Mapping(jobs, "bump-version"), "needs"));

        var publish = Mapping(jobs, "publish-image");
        Assert.Equal($"./.github/workflows/{DockerPublish}", Scalar(publish, "uses"));
        Assert.Contains("tests", Sequence(publish, "needs"));
        Assert.Contains("container-tests", Sequence(publish, "needs"));
        var condition = Scalar(publish, "if")!;
        Assert.Contains("needs.tests.result == 'success'", condition);
        Assert.Contains("needs.container-tests.result == 'success'", condition);
    }

    [Fact]
    public void DockerPublish_RunsOnlyWhenCalledAfterTheTestsOrByHand()
    {
        // A workflow_run or push trigger would publish without waiting for the tests, and a pull request
        // build would be one more image build per pull request (the container test already builds one).
        Assert.Equal(["workflow_call", "workflow_dispatch"], Triggers(Load(DockerPublish)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DockerPublish_SmokeTestsEachPlatformNativelyBeforePushingIt()
    {
        var build = Mapping(Mapping(Load(DockerPublish), "jobs"), "build");
        var steps = JobSteps(build).ToList();

        var smokeTest = steps.FindIndex(step => Scalar(step, "run")?.Contains("container-smoke-test.sh") == true);
        var push = steps.FindIndex(step => step.Children.TryGetValue(new YamlScalarNode("with"), out var with)
            && Scalar(with, "outputs")?.Contains("push=true") == true);
        Assert.InRange(smokeTest, 0, int.MaxValue);
        Assert.InRange(push, smokeTest + 1, int.MaxValue);
        Assert.Equal("${{ matrix.runner }}", Scalar(build, "runs-on"));
        Assert.Contains("ubuntu-24.04-arm", File.ReadAllText(Path.Join(WorkflowDirectory, DockerPublish)));
    }

    [Theory]
    [MemberData(nameof(WorkflowFiles))]
    public void DockerBuilds_UseDockerfileDotnetAndCacheEveryStage(string file)
    {
        foreach (var (job, step) in Steps(Load(file)).Where(s => Scalar(s.Step, "uses")?.StartsWith("docker/build-push-action@", StringComparison.Ordinal) == true))
        {
            var with = Mapping(step, "with");
            Assert.Equal("./Dockerfile.dotnet", Scalar(with, "file"));
            if (Scalar(with, "cache-to") is { } cacheTo)
            {
                // Without mode=max only the final image's layers are cached, never the SDK build stage.
                Assert.Contains("mode=max", cacheTo);
            }
        }
    }

    [Fact]
    public void ContainerTestAndPublish_ShareTheAmd64BuildCache()
    {
        // The container test builds the amd64 image first; publishing it reuses those layers only from the same scope.
        static YamlMappingNode FirstBuild(YamlMappingNode workflow) => Mapping(
            Steps(workflow).First(s => Scalar(s.Step, "uses")?.StartsWith("docker/build-push-action@", StringComparison.Ordinal) == true).Step,
            "with");

        var published = FirstBuild(Load(DockerPublish));
        var tested = FirstBuild(Load(ContainerTests));

        foreach (var cache in new[] { "cache-from", "cache-to" })
        {
            Assert.Equal(Scalar(published, cache)!.Replace("${{ matrix.arch }}", "amd64", StringComparison.Ordinal), Scalar(tested, cache));
        }
    }

    [Theory]
    [InlineData("build-windows-release.yml", "windows-release")]
    [InlineData("build-android-apk.yml", "android-release")]
    public void ReleaseDownloads_AreBuiltForEveryReleaseTag(string file, string job)
    {
        // A tag pushed with GITHUB_TOKEN starts no workflow, so a tag trigger never ran for a release.
        var workflow = Load(file);
        Assert.DoesNotContain("push", Triggers(workflow));
        var tag = Mapping(Mapping(Mapping(Mapping(workflow, "on"), "workflow_call"), "inputs"), "tag");
        Assert.Equal("true", Scalar(tag, "required"));

        var caller = Mapping(Mapping(Load(Release), "jobs"), job);
        Assert.Equal($"./.github/workflows/{file}", Scalar(caller, "uses"));
        Assert.Equal("v${{ needs.bump-version.outputs.version }}", Scalar(Mapping(caller, "with"), "tag"));
        Assert.Contains("bump-version", Sequence(caller, "needs"));
    }

    [Fact]
    public void Release_RunsOneAtATimeAndHonoursSkipVersionBump()
    {
        var release = Load(Release);

        var concurrency = Mapping(release, "concurrency");
        Assert.Equal("false", Scalar(concurrency, "cancel-in-progress"));
        var bump = Mapping(Mapping(release, "jobs"), "bump-version");
        Assert.Contains("!contains(github.event.head_commit.message, '[skip-version-bump]')", Scalar(bump, "if"));
        // The bump commit is marked so that pushing it never starts another release.
        Assert.Contains(Steps(release), s => Scalar(s.Step, "run")?.Contains("git commit -m \"Bump version to $NEW_VERSION [skip-version-bump]\"") == true);
        // Releasing only the tested commit: the bump and its tag are pushed together, onto the tested commit.
        Assert.Contains(Steps(release), s => Scalar(s.Step, "run")?.Contains("git push --atomic origin \"HEAD:refs/heads/dotnet\"") == true);
    }

    [Fact]
    public void Release_AVersionWithNoTagYet_IsReleasedAsItStandsBeforeAnyIncrement()
    {
        var steps = Steps(Load(Release)).ToList();
        var bump = Scalar(Assert.Single(steps, s => Scalar(s.Step, "id") == "bump").Step, "run")!;
        var push = Scalar(Assert.Single(steps, s => Scalar(s.Step, "id") == "push").Step, "run")!;

        var tagCheck = bump.IndexOf("refs/tags/v${CURRENT_VERSION}", StringComparison.Ordinal);
        var increment = bump.IndexOf("BASH_REMATCH[3] + 1", StringComparison.Ordinal);

        Assert.InRange(tagCheck, 0, increment);
        Assert.Contains("new_version=$CURRENT_VERSION", bump);
        // The project already holds the version, so there must be nothing to commit for it.
        Assert.Contains("if [[ \"$BUMPED\" == \"true\" ]]", push);
    }

    [Theory]
    [MemberData(nameof(WorkflowFiles))]
    public void Workflows_UseMaintainedActionsAndLinkTheDotnetBranch(string file)
    {
        var text = File.ReadAllText(Path.Join(WorkflowDirectory, file));

        Assert.DoesNotContain("actions/create-release@", text);
        Assert.DoesNotContain("/blob/master/", text);
        Assert.DoesNotContain("on merge to master", text);
    }

    [Fact]
    public void SecurityScan_ChecksTheDotnetCodeOnEveryChangeAndWeekly()
    {
        var scan = Load(SecurityScan);
        var on = Mapping(scan, "on");

        Assert.Equal(["dotnet"], Sequence(Mapping(on, "push"), "branches"));
        Assert.Equal(["dotnet"], Sequence(Mapping(on, "pull_request"), "branches"));
        Assert.NotEmpty(Sequence(on, "schedule"));
        var text = File.ReadAllText(Path.Join(WorkflowDirectory, SecurityScan));
        Assert.Contains("package --vulnerable --include-transitive --format json", text);
        Assert.Contains("node scripts/check-vulnerable-packages.js", text);
        Assert.DoesNotMatch(@"\b(pip|bandit|requirements\.txt)\b", text);
    }

    [Theory]
    [InlineData(ContainerTests)]
    [InlineData(SecurityScan)]
    public void ImageScans_FailOnFixableCriticalAndHighVulnerabilities(string file)
    {
        var gates = Steps(Load(file))
            .Where(s => Scalar(s.Step, "uses")?.StartsWith("aquasecurity/trivy-action@", StringComparison.Ordinal) == true)
            .Select(s => Mapping(s.Step, "with"))
            .Where(with => Scalar(with, "exit-code") == "1")
            .ToList();

        // The report keeps to the severities it asks for; by default a SARIF report ignores them.
        var report = Mapping(Assert.Single(Steps(Load(file)), s => Scalar(s.Step, "name")?.Contains("(report)") == true).Step, "with");
        Assert.Equal("true", Scalar(report, "limit-severities-for-sarif"));

        var gate = Assert.Single(gates);
        Assert.Equal("CRITICAL,HIGH", Scalar(gate, "severity"));
        Assert.Equal("true", Scalar(gate, "ignore-unfixed"));
    }

    [Fact]
    public void UnitTests_FailOnLowerCoverageWarningsAndHangs()
    {
        var unitTests = Mapping(Mapping(Load(Tests), "jobs"), "unit-tests");
        var steps = JobSteps(unitTests).ToList();

        Assert.InRange(int.Parse(Scalar(unitTests, "timeout-minutes")!), 1, 30);
        var test = Assert.Single(steps, step => Scalar(step, "run")?.Contains("--filter \"Category!=Integration\"") == true);
        Assert.Contains("--blame-hang-timeout", Scalar(test, "run"));
        var build = Assert.Single(steps, step => Scalar(step, "name")?.StartsWith("Build", StringComparison.Ordinal) == true);
        Assert.Equal(3, Regex.Matches(Scalar(build, "run")!, "-p:StrictWarnings=true").Count);

        var coverage = Mapping(Assert.Single(steps, step => Scalar(step, "uses")?.StartsWith("irongut/CodeCoverageSummary@", StringComparison.Ordinal) == true), "with");
        Assert.Equal("true", Scalar(coverage, "fail_below_min"));
        // The lower threshold is the gate; the action silently replaces an upper threshold that is below it.
        var thresholds = Regex.Match(Scalar(coverage, "thresholds")!, @"^(\d+) (\d+)$");
        Assert.True(thresholds.Success, "thresholds must be '<lower> <upper>'");
        var lower = int.Parse(thresholds.Groups[1].Value);
        Assert.InRange(lower, 97, 100);
        Assert.InRange(int.Parse(thresholds.Groups[2].Value), lower + 1, 100);
        Assert.DoesNotContain("../blob/", File.ReadAllText(Path.Join(WorkflowDirectory, Tests)));
    }

    [Fact]
    public void WindowsTests_RunTheWholeSuite()
    {
        var windows = Mapping(Mapping(Load(Tests), "jobs"), "windows-tests");

        Assert.Equal("windows-latest", Scalar(windows, "runs-on"));
        var test = Assert.Single(JobSteps(windows), step => Scalar(step, "run")?.Contains("dotnet test") == true);
        Assert.DoesNotContain("--filter", Scalar(test, "run"));
    }

    [Fact]
    public void PrTestCheck_GatesPullRequestsIntoDotnet()
    {
        var check = Load(PrTestCheck);

        Assert.Contains("dotnet", Sequence(Mapping(Mapping(check, "on"), "pull_request"), "branches"));
        var steps = Steps(check).Select(s => s.Step).ToList();
        Assert.Contains(steps, step => Scalar(step, "run")?.Contains("exit 1") == true);
        // The changed files are listed with git rather than a third-party action.
        Assert.DoesNotContain(steps, step => Scalar(step, "uses")?.StartsWith("tj-actions/", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Dependabot_IsNotToldToSkipSharpCompressUpdates()
    {
        // SharpCompress opens every untrusted archive and ships its fixes in 0.x minor versions, and an ignore rule
        // would also suppress Dependabot's security updates for it.
        var stream = new YamlStream();
        using (var reader = new StringReader(File.ReadAllText(Path.Join(Path.GetDirectoryName(WorkflowDirectory)!, "dependabot.yml"))))
        {
            stream.Load(reader);
        }

        var updates = Assert.IsType<YamlSequenceNode>(((YamlMappingNode)stream.Documents[0].RootNode).Children[new YamlScalarNode("updates")]);
        var ignored = updates.Children.Cast<YamlMappingNode>()
            .Where(update => update.Children.ContainsKey(new YamlScalarNode("ignore")))
            .SelectMany(update => ((YamlSequenceNode)update.Children[new YamlScalarNode("ignore")]).Children.Cast<YamlMappingNode>())
            .Select(rule => Scalar(rule, "dependency-name"));

        Assert.DoesNotContain("SharpCompress", ignored);
    }

    [Fact]
    public void StrictWarnings_LeaveNuGetAuditWarningsToTheSecurityScan()
    {
        // Restore warnings are replayed by the build, so an advisory in a transitive package, or a NuGet audit source
        // that cannot be reached, would otherwise fail unrelated pull requests. security-scan.yml gates those instead.
        var repositoryRoot = Path.GetDirectoryName(Path.GetDirectoryName(WorkflowDirectory))!;
        var props = File.ReadAllText(Path.Join(repositoryRoot, "src", "Directory.Build.props"));
        var exempt = Regex.Match(props, "<WarningsNotAsErrors>(.*?)</WarningsNotAsErrors>").Groups[1].Value.Split(';');

        Assert.All(new[] { "NU1900", "NU1901", "NU1902", "NU1903", "NU1904", "NU1905" }, code => Assert.Contains(code, exempt));
    }

    [Fact]
    public void ContainerSmokeTest_ReadsTheLogsBeforeSearchingThem()
    {
        // The script runs under pipefail, where grep -q stopping at its first match can end a still-writing docker logs
        // with SIGPIPE. The check then reports no match: a release is blocked, or a warning goes unnoticed.
        var repositoryRoot = Path.GetDirectoryName(Path.GetDirectoryName(WorkflowDirectory))!;
        var script = File.ReadAllText(Path.Join(repositoryRoot, "scripts", "container-smoke-test.sh"));

        Assert.Contains("set -euo pipefail", script);
        Assert.DoesNotMatch(@"docker logs[^\n]*\|\s*grep\b", script);
        Assert.Contains("grep -q \"$@\" <<<\"${logs}\"", script);
    }

    private static string WorkflowDirectory
    {
        get
        {
            var current = AppContext.BaseDirectory;
            for (var i = 0; i < 10 && current != null; i++)
            {
                var candidate = Path.Join(current, ".github", "workflows");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
                current = Path.GetDirectoryName(current);
            }
            throw new DirectoryNotFoundException("Could not locate .github/workflows relative to test binaries.");
        }
    }

    private static YamlMappingNode Load(string file)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(File.ReadAllText(Path.Join(WorkflowDirectory, file))))
        {
            stream.Load(reader);
        }
        return Assert.IsType<YamlMappingNode>(Assert.Single(stream.Documents).RootNode);
    }

    private static YamlMappingNode Mapping(YamlNode node, string key)
    {
        var mapping = Assert.IsType<YamlMappingNode>(node);
        Assert.True(mapping.Children.TryGetValue(new YamlScalarNode(key), out var value), $"Expected a '{key}' entry");
        return Assert.IsType<YamlMappingNode>(value);
    }

    private static string? Scalar(YamlNode node, string key) =>
        node is YamlMappingNode mapping && mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static List<string?> Sequence(YamlNode node, string key)
    {
        var mapping = Assert.IsType<YamlMappingNode>(node);
        Assert.True(mapping.Children.TryGetValue(new YamlScalarNode(key), out var value), $"Expected a '{key}' entry");
        return value switch
        {
            YamlSequenceNode sequence => sequence.Children.Select(item => item is YamlScalarNode s ? s.Value : null).ToList()!,
            YamlScalarNode scalar => [scalar.Value],
            _ => throw new Xunit.Sdk.XunitException($"'{key}' is neither a list nor a value"),
        };
    }

    private static List<string> Triggers(YamlMappingNode workflow) =>
        workflow.Children[new YamlScalarNode("on")] switch
        {
            YamlMappingNode on => on.Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value!).ToList(),
            YamlSequenceNode on => on.Children.Cast<YamlScalarNode>().Select(key => key.Value!).ToList(),
            YamlScalarNode on => [on.Value!],
            _ => [],
        };

    private static IEnumerable<(string Job, YamlMappingNode Step)> Steps(YamlMappingNode workflow)
    {
        foreach (var (name, job) in Mapping(workflow, "jobs").Children)
        {
            if (job is YamlMappingNode definition && definition.Children.TryGetValue(new YamlScalarNode("steps"), out var steps))
            {
                foreach (var step in ((YamlSequenceNode)steps).Children.Cast<YamlMappingNode>())
                {
                    yield return (((YamlScalarNode)name).Value!, step);
                }
            }
        }
    }

    /// <summary>The steps of one job, in order.</summary>
    private static IEnumerable<YamlMappingNode> JobSteps(YamlMappingNode job)
    {
        Assert.True(job.Children.TryGetValue(new YamlScalarNode("steps"), out var steps), "Expected the job to have steps");
        return Assert.IsType<YamlSequenceNode>(steps).Children.Cast<YamlMappingNode>();
    }
}
