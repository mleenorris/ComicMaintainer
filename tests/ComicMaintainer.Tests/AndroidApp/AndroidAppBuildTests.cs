using System.Text.RegularExpressions;
using System.Xml.Linq;
using ComicMaintainer.MauiApp.ViewModels;

namespace ComicMaintainer.Tests.AndroidApp;

/// <summary>
/// How the Android app is built and released, none of which can run without the Android workload and SDK: the main
/// solution must build without them, and a release has to produce an APK that can be installed.
/// </summary>
public sealed class AndroidAppBuildTests
{
    private const string AppSolutionPath = "src/ComicMaintainer.Android.sln";
    private const string ProjectPath = @"ComicMaintainer.MauiApp\ComicMaintainer.MauiApp.csproj";

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

    // A second solution beside it would make the documented bare "dotnet build" and "dotnet test" fail with MSB1011.
    [Fact]
    public void TheRepositoryRoot_HoldsOnlyTheMainSolution()
    {
        var solution = Assert.Single(Directory.GetFiles(RepositoryRoot, "*.sln"));

        Assert.Equal("ComicMaintainer.sln", Path.GetFileName(solution));
    }

    // The command line skips a line it does not expect, but other tools that read solution files are stricter.
    [Theory]
    [InlineData("ComicMaintainer.sln")]
    [InlineData(AppSolutionPath)]
    public void ASolution_ClosesEachProjectExactlyOnce(string solutionFile)
    {
        var lines = File.ReadAllLines(Path.Join(RepositoryRoot, solutionFile));

        Assert.Equal(lines.Count(line => line.StartsWith("Project(", StringComparison.Ordinal)), lines.Count(line => line == "EndProject"));
    }

    [Fact]
    public void TheAppHasASolutionOfItsOwn()
    {
        var solution = File.ReadAllText(Path.Join(RepositoryRoot, AppSolutionPath));

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

        Assert.Contains($"dotnet restore {AppSolutionPath}", workflow, StringComparison.Ordinal);
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

    // Each release must be signed with the same key to install over the one before, and the password must reach MSBuild
    // through the environment rather than on a command line that could be logged.
    [Fact]
    public void TheWorkflow_SignsWithTheProjectKeyFromSecretsWhenOneIsSet()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("${{ secrets.ANDROID_KEYSTORE_BASE64 }}", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:AndroidKeyStore=true", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASSWORD", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:AndroidSigningKeyPass=env:ANDROID_KEYSTORE_PASSWORD", workflow, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"AndroidSigning(Store|Key)Pass=\$\{\{", workflow);
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

    // A font that is named but not shipped fails only on the device, as a logged warning and the system font instead.
    [Fact]
    public void EveryFontTheAppNames_ShipsWithIt()
    {
        var program = File.ReadAllText(Path.Join(AppDirectory, "MauiProgram.cs"));
        var styles = File.ReadAllText(Path.Join(AppDirectory, "Resources", "Styles", "Styles.xaml"));

        var registered = Regex.Matches(program, "AddFont\\(\"(?<file>[^\"]+)\",\\s*\"(?<alias>[^\"]+)\"\\)").ToList();
        var used = Regex.Matches(styles, "Property=\"FontFamily\"\\s+Value=\"(?<alias>[^\"]+)\"").Select(match => match.Groups["alias"].Value);

        Assert.All(registered, match => Assert.True(File.Exists(Path.Join(AppDirectory, "Resources", "Fonts", match.Groups["file"].Value)), match.Value));
        Assert.All(used, alias => Assert.Contains(registered, match => match.Groups["alias"].Value == alias));
    }

    // The pages are only compiled when a release builds the app, and bindings with a Source not even then.
    [Theory]
    [InlineData("LoginPage.xaml", typeof(LoginViewModel))]
    [InlineData("MainPage.xaml", typeof(FilesViewModel))]
    [InlineData("SettingsPage.xaml", typeof(SettingsViewModel))]
    public void EveryBindingOfAPage_NamesAPropertyOfItsViewModel(string page, Type viewModel)
    {
        var paths = XDocument.Load(Path.Join(AppDirectory, "Views", page)).Descendants()
            .SelectMany(element => element.Attributes().Select(attribute => ViewModelBindingPath(element, attribute.Value)))
            .OfType<string>()
            .ToList();

        Assert.NotEmpty(paths);
        Assert.All(paths, path => Assert.NotNull(viewModel.GetProperty(path)));
    }

    // Settings opens from the library, so someone who cannot connect has only the sign-in page to find out why.
    [Fact]
    public void TheSignInPage_OffersTheConnectionTest()
    {
        var page = File.ReadAllText(Path.Join(AppDirectory, "Views", "LoginPage.xaml"));

        Assert.Contains("Command=\"{Binding TestConnectionCommand}\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedSdkBackupFiles_AreIgnored()
    {
        var ignored = File.ReadAllLines(Path.Join(RepositoryRoot, ".gitignore")).Select(line => line.Trim());

        Assert.Contains("*.tmp", ignored);
    }

    /// <summary>The view model property an attribute is bound to, or null if it is not bound to the view model.</summary>
    private static string? ViewModelBindingPath(XElement element, string value)
    {
        var relativeToViewModel = Regex.Match(value, @"^\{Binding Source=\{RelativeSource AncestorType=\{x:Type viewmodels:\w+\}\}, Path=(?<path>\w+)\}$");
        if (relativeToViewModel.Success)
        {
            return relativeToViewModel.Groups["path"].Value;
        }

        // Inside a DataTemplate a plain binding is relative to the list's item.
        if (element.AncestorsAndSelf().Any(ancestor => ancestor.Name.LocalName == "DataTemplate"))
        {
            return null;
        }

        var plain = Regex.Match(value, @"^\{Binding (?<path>\w+)[,}]");
        return plain.Success ? plain.Groups["path"].Value : null;
    }

    private static XDocument LoadManifest() =>
        XDocument.Load(Path.Join(AppDirectory, "Platforms", "Android", "AndroidManifest.xml"));

    private static string ReadApplicationAttribute(string name)
    {
        XNamespace android = "http://schemas.android.com/apk/res/android";
        return LoadManifest().Root!.Element("application")!.Attribute(android + name)!.Value;
    }
}
