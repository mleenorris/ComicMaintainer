namespace ComicMaintainer.Core.Models;

/// <summary>
/// Per-user UI preferences for the web interface.
/// </summary>
/// <remarks>
/// Every preference is nullable so that "not set by the user" is distinguishable
/// from "explicitly set to the default value". Unset preferences fall back to the
/// application-level defaults exposed by <c>AppSettings</c>.
/// </remarks>
public class UserPreferences
{
    /// <summary>Identity of the user these preferences belong to.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>UI theme, either <c>light</c> or <c>dark</c>.</summary>
    public string? Theme { get; set; }

    /// <summary>Number of items rendered per page in the library.</summary>
    public int? PerPage { get; set; }

    /// <summary>Default reading mode, either <c>manga</c> or <c>webcomic</c>.</summary>
    public string? ReadingMode { get; set; }

    /// <summary>Library layout, either <c>files</c> (folder view) or <c>series</c>.</summary>
    public string? LibraryViewMode { get; set; }

    /// <summary>Active library filter (e.g. <c>all</c>, <c>unmarked</c>, <c>duplicates</c>).</summary>
    public string? FilterMode { get; set; }

    /// <summary>Active library sort (e.g. <c>name</c>, <c>date</c>, <c>size</c>).</summary>
    public string? SortMode { get; set; }
}
