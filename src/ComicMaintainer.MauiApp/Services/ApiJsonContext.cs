using System.Text.Json.Serialization;
using ComicMaintainer.MauiApp.Models;

namespace ComicMaintainer.MauiApp.Services;

/// <summary>
/// The JSON the app exchanges with the server. It is source-generated because a release build trims the app, and
/// trimming can disable or break reflection-based serialization, which would fail every request only in the APK.
/// </summary>
[JsonSerializable(typeof(FilesPage))]
[JsonSerializable(typeof(JobStatusInfo))]
[JsonSerializable(typeof(AccountInfo))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(SelectedFilesRequest))]
[JsonSerializable(typeof(JobStartedResponse))]
[JsonSerializable(typeof(VersionResponse))]
[JsonSerializable(typeof(SetupRequiredResponse))]
[JsonSerializable(typeof(StoredCredentialDocument))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;
