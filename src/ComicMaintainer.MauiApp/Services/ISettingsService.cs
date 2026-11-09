namespace ComicMaintainer.MauiApp.Services;

/// <summary>The app's non-secret settings. Credentials are kept by <see cref="ICredentialStore"/>.</summary>
public interface ISettingsService
{
    /// <summary>The server's address, in the form <see cref="ServerAddress.TryParse"/> produces, or empty before one is set.</summary>
    string ServerUrl { get; set; }
}
