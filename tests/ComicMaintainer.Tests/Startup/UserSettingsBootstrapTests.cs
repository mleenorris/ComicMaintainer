using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// Tests for the pre-host user-settings.json bootstrap (log size limit and the
/// one-time migration from the legacy flat file to the "AppSettings" shape).
/// </summary>
public sealed class UserSettingsBootstrapTests : IDisposable
{
    private const long DefaultLogMaxBytes = 10_485_760;

    // UserSettingsBootstrap is internal to the WebApi assembly (no InternalsVisibleTo).
    private static readonly Type BootstrapType = typeof(Program).Assembly
        .GetType("ComicMaintainer.WebApi.Startup.UserSettingsBootstrap", throwOnError: true)!;

    private static readonly Func<string, long> LoadLogMaxBytes = BootstrapType
        .GetMethod("LoadLogMaxBytes", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Func<string, long>>();

    private static readonly Action<string> EnsureUserSettingsFileMigrated = BootstrapType
        .GetMethod("EnsureUserSettingsFileMigrated", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Action<string>>();

    private readonly string _root = Path.Join(Path.GetTempPath(), "cm-usersettings-" + Guid.NewGuid().ToString("N"));
    private readonly string _settingsPath;

    public UserSettingsBootstrapTests()
    {
        Directory.CreateDirectory(_root);
        _settingsPath = Path.Join(_root, "user-settings.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void LoadLogMaxBytes_WhenTheFileDoesNotExist_ReturnsTheTenMegabyteDefault()
    {
        Assert.Equal(DefaultLogMaxBytes, LoadLogMaxBytes(_settingsPath));
    }

    [Theory]
    [InlineData("{\"AppSettings\":{\"LogMaxBytes\":2048}}", 2048)]
    [InlineData("{\"LogMaxBytes\":4096}", 4096)]
    [InlineData("{\"AppSettings\":{\"LogMaxBytes\":1024},\"LogMaxBytes\":4096}", 1024)]
    [InlineData("{\"AppSettings\":{\"LogMaxBytes\":\"2048\"},\"LogMaxBytes\":4096}", 4096)]
    [InlineData("{\"AppSettings\":{\"LogMaxBytes\":5000000000},\"LogMaxBytes\":4096}", 4096)]
    [InlineData("{\"AppSettings\":\"not-an-object\",\"LogMaxBytes\":4096}", 4096)]
    [InlineData("{\"AppSettings\":{\"FilenameFormat\":\"x\"},\"LogMaxBytes\":4096}", 4096)]
    public void LoadLogMaxBytes_PrefersTheAppSettingsValueAndFallsBackToTheLegacyKey(string json, long expected)
    {
        File.WriteAllText(_settingsPath, json);

        Assert.Equal(expected, LoadLogMaxBytes(_settingsPath));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"AppSettings\":{}}")]
    [InlineData("{\"AppSettings\":{\"LogMaxBytes\":\"big\"}}")]
    [InlineData("{\"LogMaxBytes\":\"4096\"}")]
    [InlineData("{\"LogMaxBytes\":5000000000}")]
    [InlineData("{\"LogMaxBytes\":1.5}")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"just a string\"")]
    [InlineData("{ this is not json")]
    [InlineData("")]
    public void LoadLogMaxBytes_WithoutAUsableValue_ReturnsTheDefault(string json)
    {
        File.WriteAllText(_settingsPath, json);

        Assert.Equal(DefaultLogMaxBytes, LoadLogMaxBytes(_settingsPath));
    }

    [Fact]
    public void EnsureMigrated_WhenTheFileDoesNotExist_CreatesAnEmptyAppSettingsObject()
    {
        EnsureUserSettingsFileMigrated(_settingsPath);

        AssertIsEmptyAppSettingsWrapper(File.ReadAllText(_settingsPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t  ")]
    public void EnsureMigrated_WhenTheFileIsBlank_ReplacesItWithAnEmptyAppSettingsObject(string content)
    {
        File.WriteAllText(_settingsPath, content);

        EnsureUserSettingsFileMigrated(_settingsPath);

        AssertIsEmptyAppSettingsWrapper(File.ReadAllText(_settingsPath));
    }

    [Theory]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\n  \"AppSettings\": {\n    \"FilenameFormat\": \"{series} #{issue}\"\n  }\n}")]
    [InlineData("{ this is not json")]
    public void EnsureMigrated_LeavesFilesThatAreNotLegacySettingsUntouched(string content)
    {
        File.WriteAllText(_settingsPath, content);

        EnsureUserSettingsFileMigrated(_settingsPath);

        Assert.Equal(content, File.ReadAllText(_settingsPath));
    }

    [Fact]
    public void EnsureMigrated_WrapsALegacyFlatFileUnderAppSettings()
    {
        const string legacy = """
            {
              "FilenameFormat": "{series} #{issue}",
              "LogMaxBytes": 1234,
              "WatcherEnableRename": false,
              "Tags": ["a", "b"],
              "Nested": { "Key": null }
            }
            """;
        File.WriteAllText(_settingsPath, legacy);

        EnsureUserSettingsFileMigrated(_settingsPath);

        var migrated = JsonNode.Parse(File.ReadAllText(_settingsPath))!.AsObject();
        Assert.Equal(new[] { "AppSettings" }, migrated.Select(p => p.Key));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(legacy), migrated["AppSettings"]));
        Assert.False(File.Exists(_settingsPath + ".migrating"));
        // The migrated file is read through the new shape from now on.
        Assert.Equal(1234, LoadLogMaxBytes(_settingsPath));

        // A second run sees the new shape and leaves the file alone.
        var afterFirstRun = File.ReadAllText(_settingsPath);
        EnsureUserSettingsFileMigrated(_settingsPath);
        Assert.Equal(afterFirstRun, File.ReadAllText(_settingsPath));
    }

    [Fact]
    public void EnsureMigrated_WhenTheTemporaryFileCannotBeWritten_LeavesTheLegacyFileUntouched()
    {
        const string legacy = "{\"FilenameFormat\":\"{series}\"}";
        File.WriteAllText(_settingsPath, legacy);
        // A directory where the temp file should go makes the atomic write fail.
        Directory.CreateDirectory(_settingsPath + ".migrating");

        EnsureUserSettingsFileMigrated(_settingsPath);

        Assert.Equal(legacy, File.ReadAllText(_settingsPath));
        Assert.True(Directory.Exists(_settingsPath + ".migrating"));
    }

    [Fact]
    public void EnsureMigrated_WhenThePathIsADirectory_DoesNotThrow()
    {
        Directory.CreateDirectory(_settingsPath);

        var exception = Record.Exception(() => EnsureUserSettingsFileMigrated(_settingsPath));

        Assert.Null(exception);
        Assert.True(Directory.Exists(_settingsPath));
        Assert.Equal(DefaultLogMaxBytes, LoadLogMaxBytes(_settingsPath));
    }

    private static void AssertIsEmptyAppSettingsWrapper(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(new[] { "AppSettings" }, root.EnumerateObject().Select(p => p.Name));
        var appSettings = root.GetProperty("AppSettings");
        Assert.Equal(JsonValueKind.Object, appSettings.ValueKind);
        Assert.Empty(appSettings.EnumerateObject());
    }
}
