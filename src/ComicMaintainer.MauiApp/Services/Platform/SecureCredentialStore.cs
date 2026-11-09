namespace ComicMaintainer.MauiApp.Services.Platform;

/// <summary>Keeps the credential in SecureStorage, which on Android encrypts it with a key held by the Android Keystore.</summary>
public sealed class SecureCredentialStore : ICredentialStore
{
    private const string StorageKey = "comicmaintainer.credential";

    /// <inheritdoc/>
    public async Task<ApiCredential?> LoadAsync()
    {
        try
        {
            return ApiCredential.Deserialize(await SecureStorage.Default.GetAsync(StorageKey));
        }
        catch (Exception)
        {
            // The platform's keystore fails with exceptions of its own. A credential that cannot be read is no credential.
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(ApiCredential credential)
    {
        try
        {
            await SecureStorage.Default.SetAsync(StorageKey, credential.Serialize());
        }
        catch (Exception exception)
        {
            throw new ApiException(
                ApiErrorKind.CredentialStorage,
                "This device could not store your sign-in securely, so you were not signed in.",
                innerException: exception);
        }
    }

    /// <inheritdoc/>
    public Task ClearAsync()
    {
        try
        {
            SecureStorage.Default.Remove(StorageKey);
        }
        catch (Exception)
        {
            // Nothing more can be done, and the credential is already out of use in memory.
        }

        return Task.CompletedTask;
    }
}
