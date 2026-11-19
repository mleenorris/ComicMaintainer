using System.Text.Json.Serialization;

namespace ComicMaintainer.Core.Models;

/// <summary>
/// Where the ComicVine issue id in a file's tags comes from (see
/// <see cref="Utilities.ComicVineTagId.Read"/>), which decides how far it is trusted.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ComicVineIdSource>))]
public enum ComicVineIdSource
{
    /// <summary>The tags name no ComicVine issue.</summary>
    None = 0,

    /// <summary>A tagger's id in the notes (ComicTagger's <c>[CVDB12345]</c>): trusted.</summary>
    Tagger = 1,

    /// <summary>
    /// This application's own record of a name match (<c>[CVDB12345] (matched by name in
    /// ComicMaintainer)</c>): matched again, so a volume the user sets takes effect.
    /// </summary>
    NameMatch = 2,

    /// <summary>
    /// Only an issue page in the web link. Taggers write one, and so did earlier versions of this
    /// application after a name match: it is that application's own when the file was filled from
    /// that very issue.
    /// </summary>
    WebOnly = 3,
}
