namespace ComicMaintainer.WebApi.Startup;

/// <summary>
/// Reads a secret from an environment variable or, Docker-secrets style, from the file named by the same variable
/// with a <c>_FILE</c> suffix: <c>JWT_SECRET_FILE=/run/secrets/jwt_secret</c> instead of <c>JWT_SECRET=…</c>.
/// See docs/DOCKER_DEPLOYMENT_DOTNET.md.
/// </summary>
internal static class SecretEnvironmentVariable
{
    /// <summary>The suffix of the variable that names a file holding the secret.</summary>
    public const string FileSuffix = "_FILE";

    /// <summary>
    /// The secret held by <paramref name="name"/>, or by the file that <paramref name="name"/><c>_FILE</c> names
    /// (without the line break that ends the file). An empty or blank variable counts as not set.
    /// </summary>
    /// <param name="source">
    /// Where the secret came from, for the startup log: the variable, or the file variable and the path. Never the
    /// secret itself.
    /// </param>
    /// <returns>The secret, or <see langword="null"/> when neither variable is set.</returns>
    /// <exception cref="InvalidOperationException">
    /// Both variables are set, or the file cannot be read or is empty.
    /// </exception>
    public static string? Read(string name, out string? source)
    {
        var fileVariable = name + FileSuffix;
        var value = Environment.GetEnvironmentVariable(name);
        var path = Environment.GetEnvironmentVariable(fileVariable)?.Trim();
        var hasValue = !string.IsNullOrWhiteSpace(value);
        var hasPath = !string.IsNullOrEmpty(path);

        if (hasValue && hasPath)
        {
            throw new InvalidOperationException(
                $"Both {name} and {fileVariable} are set. Set only one of them: {fileVariable} names a file, such as a " +
                $"Docker secret, that holds the value of {name}. See docs/DOCKER_DEPLOYMENT_DOTNET.md.");
        }

        if (hasValue)
        {
            source = name;
            return value;
        }

        if (!hasPath)
        {
            source = null;
            return null;
        }

        string contents;
        try
        {
            contents = File.ReadAllText(path!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException($"{fileVariable} is set to '{path}', which cannot be read: {ex.Message}", ex);
        }

        // `echo secret > file`, `openssl rand -base64 48 > file` and most editors end the file with a line break
        // that is not part of the secret.
        var secret = contents.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException($"{fileVariable} is set to '{path}', which is empty.");
        }

        source = $"{fileVariable} ({path})";
        return secret;
    }
}
