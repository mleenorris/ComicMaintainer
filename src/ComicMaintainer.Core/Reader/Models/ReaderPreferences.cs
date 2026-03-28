namespace ComicMaintainer.Core.Reader.Models;

/// <summary>
/// Represents per-user reader defaults and preferences.
/// </summary>
public class ReaderPreferences
{
    /// <summary>Identity of the user these preferences belong to.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Default reader mode applied when opening new content.</summary>
    public ReaderMode DefaultReaderMode { get; set; } = ReaderMode.SinglePage;

    /// <summary>Default reading direction applied when opening new content.</summary>
    public ReadingDirection ReadingDirection { get; set; } = ReadingDirection.LeftToRight;

    /// <summary>Whether tap zones for page navigation are enabled.</summary>
    public bool TapZonesEnabled { get; set; } = true;

    /// <summary>Whether the reader chrome (toolbar, overlays) auto-hides after inactivity.</summary>
    public bool AutoHideChrome { get; set; } = true;

    /// <summary>
    /// Preferred page fit mode, e.g. "width", "height", "original".
    /// Stored as a string for forward compatibility.
    /// </summary>
    public string FitPreference { get; set; } = "width";
}
