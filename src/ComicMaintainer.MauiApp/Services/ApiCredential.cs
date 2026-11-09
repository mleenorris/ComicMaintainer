using System.Text.Json;
using ComicMaintainer.MauiApp.Models;

namespace ComicMaintainer.MauiApp.Services;

/// <summary>What the app signs its requests with.</summary>
public enum CredentialKind
{
    /// <summary>A session token from signing in with a password, sent as a bearer token. It expires.</summary>
    Session,

    /// <summary>An API key the user created and pasted in, sent in the <c>X-API-Key</c> header. It lasts until revoked.</summary>
    ApiKey,
}

/// <summary>
/// A credential and the server that issued it. Its <see cref="ServerUrl"/> is what requests are sent to, so that a
/// token can never reach another server. Instances are compared by reference, and it is deliberately not a record: a
/// record's generated <c>ToString</c> would print the secret.
/// </summary>
public sealed class ApiCredential
{
    public ApiCredential(CredentialKind kind, string value, string serverUrl)
    {
        Kind = kind;
        Value = value;
        ServerUrl = serverUrl;
    }

    public CredentialKind Kind { get; }

    public string Value { get; }

    public string ServerUrl { get; }

    /// <summary>The form the credential is kept in on the device.</summary>
    public string Serialize() => JsonSerializer.Serialize(
        new StoredCredentialDocument { Kind = Kind.ToString(), Value = Value, Server = ServerUrl },
        ApiJsonContext.Default.StoredCredentialDocument);

    /// <summary>Reads <see cref="Serialize"/>'s output. Returns null for anything else, which then counts as signed out.</summary>
    public static ApiCredential? Deserialize(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize(stored, ApiJsonContext.Default.StoredCredentialDocument);
            return document is { Value.Length: > 0, Server.Length: > 0 } && Enum.TryParse<CredentialKind>(document.Kind, out var kind)
                ? new ApiCredential(kind, document.Value, document.Server)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Keeps the credential between runs of the app; on the device this is SecureStorage.</summary>
public interface ICredentialStore
{
    /// <summary>The stored credential, or null when there is none or it cannot be read. Does not throw.</summary>
    Task<ApiCredential?> LoadAsync();

    /// <summary>Stores <paramref name="credential"/>. Throws <see cref="ApiException"/> when the device cannot.</summary>
    Task SaveAsync(ApiCredential credential);

    /// <summary>Forgets the stored credential. Does not throw.</summary>
    Task ClearAsync();
}
