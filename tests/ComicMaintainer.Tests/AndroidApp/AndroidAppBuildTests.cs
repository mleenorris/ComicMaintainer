using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ComicMaintainer.Tests.AndroidApp;

/// <summary>
/// How the Android app is built and released, none of which can run without the Android workload and SDK: the main
/// solution must build without them, and a release has to produce an APK that can be installed.
/// </summary>
public sealed class AndroidAppBuildTests
{
    private const string ProjectPath = @"src\ComicMaintainer.MauiApp\ComicMaintainer.MauiApp.csproj";

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Join(current.FullName, "ComicMaintainer.sln")))
            {
                current = current.Parent;
            }

            return Assert.IsType<DirectoryInfo>(current).FullName;
        }
    }

    private static string AppDirectory => Path.Join(RepositoryRoot, "src", "ComicMaintainer.MauiApp");

    private static string ReadWorkflow() => File.ReadAllText(Path.Join(RepositoryRoot, ".github", "workflows", "build-android-apk.yml"));

    [Fact]
    public void TheMainSolution_DoesNotNeedTheAndroidWorkload()
    {
        var solution = File.ReadAllText(Path.Join(RepositoryRoot, "ComicMaintainer.sln"));

        Assert.DoesNotContain("MauiApp", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAppHasASolutionOfItsOwn()
    {
        var solution = File.ReadAllText(Path.Join(RepositoryRoot, "ComicMaintainer.Android.sln"));

        Assert.Contains(ProjectPath, solution, StringComparison.Ordinal);
    }

    [Fact]
    public void TheApp_DoesNotPullTheServerIntoTheApk()
    {
        var project = File.ReadAllText(Path.Join(AppDirectory, "ComicMaintainer.MauiApp.csproj"));

        Assert.DoesNotContain("ComicMaintainer.Core", project, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorkflow_RestoresTheSolutionThatContainsTheApp()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("dotnet restore ComicMaintainer.Android.sln", workflow, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"ComicMaintainer\.sln", workflow);
    }

    [Fact]
    public void TheWorkflow_PublishesAnApkInsteadOfOnlyBuilding()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("dotnet publish src/ComicMaintainer.MauiApp/ComicMaintainer.MauiApp.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("-f net10.0-android", Regex.Replace(workflow, @"`\s+", " "), StringComparison.Ordinal);
        Assert.Contains("-p:AndroidPackageFormat=apk", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet build", workflow, StringComparison.Ordinal);
    }

    // The build leaves an unsigned APK next to the signed one, and Android refuses to install it.
    [Fact]
    public void TheWorkflow_ShipsTheSignedApk()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("net10.0-android/publish\" -Filter \"*-Signed.apk\"", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("-Filter \"*.apk\"", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void TheManifest_DoesNotBackUpSecureStorageWithoutItsKey()
    {
        Assert.Equal("false", ReadApplicationAttribute("allowBackup"));
    }

    // Android blocks plain HTTP by default, which would make the usual home-network address fail with no way around it.
    [Fact]
    public void TheManifest_AllowsTheHttpAddressesSelfHostedServersUse()
    {
        Assert.Equal("true", ReadApplicationAttribute("usesCleartextTraffic"));
    }

    [Fact]
    public void TheMinimumApiLevel_IsOneWhereSecureStorageIsEncrypted()
    {
        var manifest = LoadManifest();
        XNamespace android = "http://schemas.android.com/apk/res/android";
        var minimum = manifest.Root!.Element("uses-sdk")!.Attribute(android + "minSdkVersion")!.Value;
        var project = File.ReadAllText(Path.Join(AppDirectory, "ComicMaintainer.MauiApp.csproj"));

        Assert.Equal("23", minimum);
        Assert.Contains($">{minimum}.0</SupportedOSPlatformVersion>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedSdkBackupFiles_AreIgnored()
    {
        var ignored = File.ReadAllLines(Path.Join(RepositoryRoot, ".gitignore")).Select(line => line.Trim());

        Assert.Contains("*.tmp", ignored);
    }

    private static XDocument LoadManifest() =>
        XDocument.Load(Path.Join(AppDirectory, "Platforms", "Android", "AndroidManifest.xml"));

    private static string ReadApplicationAttribute(string name)
    {
        XNamespace android = "http://schemas.android.com/apk/res/android";
        return LoadManifest().Root!.Element("application")!.Attribute(android + name)!.Value;
    }
}
