using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ComicMaintainer.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// Checks that the environment variables the documentation and the shipped deployment files tell
/// people to set are ones the app reads. An unread variable is ignored without a warning, which is
/// how the Python version's MAX_WORKERS, WEB_PORT, FILENAME_FORMAT, ISSUE_NUMBER_PADDING,
/// WATCHER_ENABLED and DB_CACHE_SIZE_MB stayed documented, and even set in the image, while doing
/// nothing. Variables handled by AppSettingsEnvironmentPostConfigure are checked by setting them and
/// seeing which setting changes, so these tests run in the non-parallel environment collection.
/// </summary>
[Collection(AppSettingsEnvironmentPostConfigureTests.EnvironmentCollection.Name)]
public sealed class DocumentedEnvironmentVariablesTests
{
    private static readonly string[] DeploymentFiles = ["Dockerfile.dotnet", "docker-compose.dotnet.yml", ".env.example"];
    private static readonly HashSet<string> DocumentationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".yml", ".yaml", ".json", ".txt", ".sh",
    };

    /// <summary>Variables read outside AppSettingsEnvironmentPostConfigure, with the file that reads each.</summary>
    private static readonly Dictionary<string, string> ReadElsewhere = new(StringComparer.Ordinal)
    {
        ["PUID"] = "entrypoint.dotnet.sh",
        ["PGID"] = "entrypoint.dotnet.sh",
        ["JWT_SECRET"] = "src/ComicMaintainer.WebApi/Startup/AuthServiceCollectionExtensions.cs",
        ["ADMIN_USERNAME"] = "src/ComicMaintainer.WebApi/Startup/WebApplicationExtensions.cs",
        ["ADMIN_EMAIL"] = "src/ComicMaintainer.WebApi/Startup/WebApplicationExtensions.cs",
        ["ADMIN_PASSWORD"] = "src/ComicMaintainer.WebApi/Startup/WebApplicationExtensions.cs",
        ["CORS_ALLOWED_ORIGINS"] = "src/ComicMaintainer.WebApi/Startup/WebServiceCollectionExtensions.cs",
    };

    /// <summary>Framework and runtime variables explicitly supported by the app or its documentation.</summary>
    private static readonly HashSet<string> FrameworkVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASPNETCORE_URLS",
        "OTEL_EXPORTER_OTLP_ENDPOINT",
        "OTEL_EXPORTER_OTLP_PROTOCOL",
        "OTEL_EXPORTER_OTLP_HEADERS",
        "OTEL_SERVICE_NAME",
    };

    /// <summary>A value of each shape a setting takes: a path or name, either flag value, and a number.</summary>
    private static readonly string[] ProbeValues = ["/documented/value", "true", "false", "12345"];

    // AppSettingsEnvironmentPostConfigure is internal to the WebApi assembly (no InternalsVisibleTo).
    private static readonly IPostConfigureOptions<AppSettings> PostConfigure = (IPostConfigureOptions<AppSettings>)Activator.CreateInstance(
        typeof(Program).Assembly.GetType("ComicMaintainer.WebApi.Startup.AppSettingsEnvironmentPostConfigure", throwOnError: true)!)!;

    [Theory]
    [InlineData("Dockerfile.dotnet")]
    [InlineData("docker-compose.dotnet.yml")]
    [InlineData(".env.example")]
    public void DeploymentFile_SetsOnlyVariablesTheAppReads(string file)
    {
        var variables = VariablesSetIn(file);

        Assert.NotEmpty(variables);
        Assert.All(variables, variable => Assert.True(IsRead(variable), $"{file} sets {variable}, which the app does not read"));
    }

    [Fact]
    public void ReadmeConfigurationOptions_ListOnlyVariablesThatSetTheirSetting()
    {
        var rows = ReadmeConfigurationOptions();

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.True(IsRead(row.Variable), $"README.md documents {row.Variable}, which the app does not read");

            if (ReadElsewhere.ContainsKey(row.Variable))
            {
                Assert.Equal(row.Variable, row.Setting);
                return;
            }

            var changed = SettingsChangedBy(row.Variable);
            Assert.NotEmpty(changed);
            Assert.Contains(
                row.Setting,
                changed);
        });
    }

    /// <summary>
    /// This version has no environment variable for these; the settings they used to control are
    /// set through the Settings page, AppSettings__&lt;Setting&gt; or the port mapping instead.
    /// </summary>
    [Theory]
    [InlineData("MAX_WORKERS")]
    [InlineData("WEB_PORT")]
    [InlineData("FILENAME_FORMAT")]
    [InlineData("ISSUE_NUMBER_PADDING")]
    [InlineData("WATCHER_ENABLED")]
    [InlineData("DB_CACHE_SIZE_MB")]
    [InlineData("GUNICORN_WORKERS")]
    public void PythonOnlyVariable_IsNotSetAnywhereInTheDocumentation(string variable)
    {
        var assignment = new Regex($@"\b{variable}\s*[=:]");

        var files = DocumentationFiles()
            .Where(file => assignment.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(RepositoryRoot, file));

        Assert.Empty(files);
    }

    [Fact]
    public void DocumentedAppSettingsVariables_NameExistingSettings()
    {
        // The configuration binder skips an AppSettings__<Name> that matches no setting without a warning.
        var mentions = DocumentationFiles()
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"\bAppSettings__([A-Za-z0-9]+)", RegexOptions.IgnoreCase)
                .Select(match => (File: Path.GetRelativePath(RepositoryRoot, file), Setting: match.Groups[1].Value)))
            .ToList();

        Assert.NotEmpty(mentions);
        Assert.All(mentions, mention => Assert.True(
            IsAppSetting(mention.Setting),
            $"{mention.File} mentions AppSettings__{mention.Setting}, but there is no {mention.Setting} setting"));
    }

    private static string RepositoryRoot
    {
        get
        {
            var current = AppContext.BaseDirectory;
            for (var i = 0; i < 10 && current != null; i++)
            {
                if (File.Exists(Path.Combine(current, "Dockerfile.dotnet")))
                {
                    return current;
                }
                current = Path.GetDirectoryName(current);
            }
            throw new DirectoryNotFoundException("Could not locate the repository root relative to test binaries.");
        }
    }

    /// <summary>
    /// The user-facing documentation and deployment files. CHANGELOG.md and docs/archive are
    /// history, so they keep the names settings had at the time.
    /// </summary>
    private static IEnumerable<string> DocumentationFiles()
    {
        var root = RepositoryRoot;
        var archive = Path.Combine(root, "docs", "archive") + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(root, "*.md")
            .Where(file => Path.GetFileName(file) != "CHANGELOG.md")
            .Concat(DeploymentFiles.Select(file => Path.Combine(root, file)))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*", SearchOption.AllDirectories)
                .Where(file => !file.StartsWith(archive, StringComparison.Ordinal))
                .Where(file => DocumentationExtensions.Contains(Path.GetExtension(file))))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.yml"));
    }

    /// <summary>
    /// The variables a deployment file sets, including the commented-out examples, which people
    /// uncomment as they are.
    /// </summary>
    private static List<string> VariablesSetIn(string file)
    {
        var assignment = file switch
        {
            "Dockerfile.dotnet" => new Regex(@"^\s*#?\s*ENV\s+(\w+)[=\s]"),
            "docker-compose.dotnet.yml" => new Regex(@"^\s*#?\s*-\s+(\w+)="),
            _ => new Regex(@"^\s*#?\s*(\w+)="),
        };

        return File.ReadLines(Path.Combine(RepositoryRoot, file))
            .Select(line => assignment.Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .ToList();
    }

    /// <summary>The Setting and Environment Variable columns of README.md's "Configuration Options" table.</summary>
    private static List<(string Setting, string Variable)> ReadmeConfigurationOptions()
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot, "README.md"));
        var heading = Array.FindIndex(lines, line => line.Trim() == "### Configuration Options");
        Assert.True(heading >= 0, "README.md has no \"### Configuration Options\" section");

        var table = lines.Skip(heading + 1)
            .SkipWhile(string.IsNullOrWhiteSpace)
            .TakeWhile(line => line.TrimStart().StartsWith('|'))
            .Select(line => line.Split('|', StringSplitOptions.TrimEntries))
            .ToList();
        Assert.True(
            table.Count > 0 && table[0].Length > 2 && table[0][1] == "Setting" && table[0][2] == "Environment Variable",
            "Expected the Configuration Options table to start with the Setting and Environment Variable columns");

        return table.Skip(2) // header and separator rows
            .Select(cells => (Setting: cells[1].Trim('`'), Variable: cells[2].Trim('`')))
            .ToList();
    }

    /// <summary>Whether the app, or the Docker image's entrypoint, reads <paramref name="variable"/>.</summary>
    private static bool IsRead(string variable)
    {
        if (ReadElsewhere.TryGetValue(variable, out var reader))
        {
            return Regex.IsMatch(File.ReadAllText(Path.Combine(RepositoryRoot, reader)), $@"\b{Regex.Escape(variable)}\b");
        }

        if (FrameworkVariables.Contains(variable))
        {
            return true;
        }

        // Section__Key is bound by the configuration system. Only the AppSettings section is checked
        // here; other sections, such as TELEMETRY__ and API__, bind their own options.
        var keys = variable.Split("__");
        if (keys[0].Equals(nameof(AppSettings), StringComparison.OrdinalIgnoreCase))
        {
            return keys.Length == 2 && IsAppSetting(keys[1]);
        }

        if (keys.Length > 1)
        {
            return true;
        }

        return SettingsChangedBy(variable).Count > 0;
    }

    private static bool IsAppSetting(string name) =>
        typeof(AppSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is { CanWrite: true };

    /// <summary>
    /// The settings that AppSettingsEnvironmentPostConfigure changes when <paramref name="variable"/>
    /// is set to any of the <see cref="ProbeValues"/>, compared with leaving it unset.
    /// </summary>
    private static SortedSet<string> SettingsChangedBy(string variable)
    {
        using var environment = new AppSettingsEnvironmentPostConfigureTests.EnvironmentScope(variable);
        var unset = ConfiguredSettings();

        var changed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var value in ProbeValues)
        {
            environment.Set(variable, value);
            changed.UnionWith(ConfiguredSettings().Where(setting => setting.Value != unset[setting.Key]).Select(setting => setting.Key));
        }

        return changed;
    }

    /// <summary>Each setting's value, as JSON, after the environment overrides are applied to the defaults.</summary>
    private static Dictionary<string, string> ConfiguredSettings()
    {
        var options = new AppSettings();
        PostConfigure.PostConfigure(Options.DefaultName, options);
        return typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(property => property.Name, property => JsonSerializer.Serialize(property.GetValue(options), property.PropertyType));
    }
}
