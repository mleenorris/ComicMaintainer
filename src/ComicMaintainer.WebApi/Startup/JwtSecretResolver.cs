using System.Security.Cryptography;

namespace ComicMaintainer.WebApi.Startup;

/// <summary>
/// The only reader of the JWT signing secret. Token validation (the JwtBearer scheme) and token signing
/// (<c>AuthService</c>, through <c>IOptions&lt;JwtSettings&gt;</c>) both use the secret it resolves, so they always
/// agree. In order of precedence, the secret is:
/// <list type="number">
/// <item><description><c>JWT_SECRET</c>, or the file named by <c>JWT_SECRET_FILE</c> (such as a Docker secret);</description></item>
/// <item><description>the <c>JwtSettings:Secret</c> setting;</description></item>
/// <item><description>a random key generated on first start and saved as <see cref="KeyFileName"/> in the config
/// directory, next to the database, so that sign-ins survive restarts.</description></item>
/// </list>
/// The placeholder secrets that were published with ComicMaintainer count as not set: anyone could sign tokens with
/// them. See docs/DOCKER_DEPLOYMENT_DOTNET.md.
/// </summary>
internal static class JwtSecretResolver
{
    /// <summary>The environment variable that holds the secret; <c>JWT_SECRET_FILE</c> names a file that does.</summary>
    public const string EnvironmentVariable = "JWT_SECRET";

    /// <summary>The setting that holds the secret when neither environment variable is set.</summary>
    public const string ConfigurationKey = "JwtSettings:Secret";

    /// <summary>The file in the config directory that keeps the generated key.</summary>
    public const string KeyFileName = "jwt-secret.key";

    // 48 random bytes, like the documented `openssl rand -base64 48`.
    private const int GeneratedKeyBytes = 48;

    // The "Secret" shipped in appsettings.json, and the fallback of docker-compose.dotnet.yml (also the value in
    // .env.example). Deployments set up with them may still pass them in.
    private static readonly string[] PublishedPlaceholders =
    [
        "YourSecretKeyHere-ChangeInProduction-MustBeAtLeast32CharactersLong!",
        "ChangeThisToASecureRandomString-MustBeAtLeast32CharactersLong!!",
    ];

    /// <exception cref="InvalidOperationException">
    /// Both <c>JWT_SECRET</c> and <c>JWT_SECRET_FILE</c> are set, the file cannot be read, or the generated key cannot
    /// be saved or read back.
    /// </exception>
    public static JwtSigningSecret Resolve(IConfiguration configuration)
    {
        var ignoredPlaceholders = new List<string>();

        var secret = SecretEnvironmentVariable.Read(EnvironmentVariable, out var source);
        if (secret is not null)
        {
            if (!IsPublishedPlaceholder(secret))
            {
                return new JwtSigningSecret(secret, source!, keyFile: null, generated: false, ignoredPlaceholders);
            }

            ignoredPlaceholders.Add(source!);
        }

        secret = configuration[ConfigurationKey];
        if (!string.IsNullOrWhiteSpace(secret))
        {
            if (!IsPublishedPlaceholder(secret))
            {
                return new JwtSigningSecret(secret, ConfigurationKey, keyFile: null, generated: false, ignoredPlaceholders);
            }

            ignoredPlaceholders.Add(ConfigurationKey);
        }

        var keyFile = Path.Join(ResolveKeyDirectory(configuration), KeyFileName);
        secret = ReadOrCreateKeyFile(keyFile, out var generated);
        return new JwtSigningSecret(secret, keyFile, keyFile, generated, ignoredPlaceholders);
    }

    private static bool IsPublishedPlaceholder(string secret) => Array.IndexOf(PublishedPlaceholders, secret.Trim()) >= 0;

    // CONFIG_DIR wins over AppSettings:ConfigDirectory, as in AppSettingsEnvironmentPostConfigure, so the key is kept
    // in the config directory the deployment chose.
    private static string ResolveKeyDirectory(IConfiguration configuration)
    {
        var configDirectory = Environment.GetEnvironmentVariable("CONFIG_DIR");
        return string.IsNullOrEmpty(configDirectory)
            ? CoreServiceCollectionExtensions.ResolveConfigDirectory(configuration)
            : configDirectory;
    }

    private static string ReadOrCreateKeyFile(string path, out bool generated)
    {
        generated = false;
        try
        {
            if (ReadKeyFile(path) is { } existing)
            {
                return existing;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                WriteOwnerOnlyFile(temporaryPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(GeneratedKeyBytes)));

                // The key appears complete, so no reader sees part of it. The move never replaces a file, so when
                // another instance sharing the config directory saved its key first, every instance uses that key.
                File.Move(temporaryPath, path, overwrite: false);
                generated = true;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another instance saved its key first; it is read back below.
            }
            finally
            {
                File.Delete(temporaryPath);
            }

            return ReadKeyFile(path) ?? throw new IOException("The file was removed while it was being read.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"No JWT secret is configured, and the signing key generated in its place cannot be saved to or read from '{path}': " +
                $"{ex.Message} Make the config directory writable, or set {EnvironmentVariable} (or " +
                $"{EnvironmentVariable}{SecretEnvironmentVariable.FileSuffix}) to a random secret of at least 32 characters, " +
                "such as the output of `openssl rand -base64 48`.",
                ex);
        }
    }

    // The saved key, or null when the file does not exist. The app never saves an empty key or a published
    // placeholder, so such a file is rejected rather than replaced: replacing it could leave instances that share the
    // config directory with different keys.
    private static string? ReadKeyFile(string path)
    {
        string contents;
        try
        {
            contents = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        var key = contents.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new IOException("The file is empty; delete it so that a new key is generated.");
        }

        if (IsPublishedPlaceholder(key))
        {
            throw new IOException(
                "The file holds a placeholder published with ComicMaintainer, which anyone could use to sign in as any " +
                "user; delete it so that a new key is generated.");
        }

        return key;
    }

    // The key is a credential: on Unix only the app's user may read it.
    private static void WriteOwnerOnlyFile(string path, string contents)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var writer = new StreamWriter(path, options);
        writer.Write(contents);
    }
}

/// <summary>
/// The secret that signs and validates the app's JWTs, resolved once at startup by <see cref="JwtSecretResolver"/>,
/// and where it came from. Not a record, so that nothing prints the value by accident.
/// </summary>
internal sealed class JwtSigningSecret
{
    internal JwtSigningSecret(string value, string source, string? keyFile, bool generated, IReadOnlyList<string> ignoredPlaceholders)
    {
        Value = value;
        Source = source;
        KeyFile = keyFile;
        Generated = generated;
        IgnoredPlaceholders = ignoredPlaceholders;
    }

    /// <summary>The secret. Never log it.</summary>
    public string Value { get; }

    /// <summary>
    /// Where the secret came from: <c>JWT_SECRET</c>, <c>JWT_SECRET_FILE (path)</c>, <c>JwtSettings:Secret</c> or the
    /// key file.
    /// </summary>
    public string Source { get; }

    /// <summary>The file that keeps the generated key, when the secret is that key.</summary>
    public string? KeyFile { get; }

    /// <summary>Whether this start generated the key and created <see cref="KeyFile"/>.</summary>
    public bool Generated { get; }

    /// <summary>The sources that held a published placeholder, which was ignored.</summary>
    public IReadOnlyList<string> IgnoredPlaceholders { get; }

    /// <summary>
    /// Reports where the secret came from on the startup log. The secret is resolved while the services are
    /// registered, before the app's logger exists, so Program calls this once the app is built.
    /// </summary>
    public void LogSource(ILogger logger)
    {
        if (IgnoredPlaceholders.Count > 0)
        {
            logger.LogWarning(
                "Ignoring the JWT secret set in {IgnoredSources}: it is a placeholder published with ComicMaintainer, so " +
                "anyone could use it to sign in as any user. Remove it, or replace it with a random secret of your own, " +
                "such as the output of `openssl rand -base64 48`.",
                string.Join(" and ", IgnoredPlaceholders));
        }

        if (KeyFile is null)
        {
            logger.LogInformation("Using the JWT secret from {JwtSecretSource}", Source);
        }
        else if (Generated)
        {
            logger.LogInformation("Generated a random JWT signing key and saved it to {KeyFile}", KeyFile);
        }
        else
        {
            logger.LogInformation("Using the JWT signing key saved in {KeyFile}", KeyFile);
        }
    }
}
