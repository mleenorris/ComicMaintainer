namespace ComicMaintainer.Core.Models;

/// <summary>
/// One value processing would change in a file: a ComicInfo.xml element (named as in the file,
/// such as <c>Series</c> or <c>Number</c>), or <c>VolumeYear</c>, which only the database keeps.
/// </summary>
/// <param name="Field">The element's name.</param>
/// <param name="Before">The value now, or null when the file has none.</param>
/// <param name="After">The value processing would leave, or null when it would remove it.</param>
public sealed record ComicInfoFieldChange(string Field, string? Before, string? After);

/// <summary>Why a file in a processing preview could not get its new name.</summary>
public enum ProcessingPreviewClash
{
    /// <summary>Nothing stands in the way.</summary>
    None,

    /// <summary>A file with the new name (ignoring case) is already there.</summary>
    ExistingFile,

    /// <summary>An earlier file of the same preview would take the new name first.</summary>
    SameTargetInPreview
}

/// <summary>
/// What processing (normalize, then rename, as a forced reprocess) would do to one file, worked
/// out without changing anything. See <c>IComicProcessorService.PreviewProcessingAsync</c>.
/// </summary>
public sealed record ProcessingPreviewItem
{
    /// <summary>The file's path now.</summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// The series the file would be filed under: its series once normalized, else its folder's
    /// name. Previews are grouped by it.
    /// </summary>
    public string? Series { get; init; }

    /// <summary>
    /// The path processing would give the file, or null when it keeps its path. With a
    /// <see cref="Clash"/> this is the name it would want, which it would not get.
    /// </summary>
    public string? NewFilePath { get; init; }

    /// <summary>The values processing would change, in the file's element order.</summary>
    public IReadOnlyList<ComicInfoFieldChange> TagChanges { get; init; } = [];

    /// <summary>
    /// Why the file would not get <see cref="NewFilePath"/>. Processing then flags it as a
    /// duplicate instead of renaming it.
    /// </summary>
    public ProcessingPreviewClash Clash { get; init; }

    /// <summary>The file that has, or would first take, <see cref="NewFilePath"/>.</summary>
    public string? ClashWith { get; init; }

    /// <summary>
    /// Something processing would leave undone without failing the rename, such as tags that are
    /// not written because a CBR is not converted to CBZ.
    /// </summary>
    public string? Warning { get; init; }

    /// <summary>Why processing would fail for this file, or null.</summary>
    public string? Error { get; init; }

    /// <summary>True when the file would be renamed: it gets a new path and nothing stops it.</summary>
    public bool Renames => NewFilePath is not null && Clash == ProcessingPreviewClash.None && Error is null;

    /// <summary>True when the preview has anything to show for the file.</summary>
    public bool HasChanges =>
        NewFilePath is not null || TagChanges.Count > 0 || Clash != ProcessingPreviewClash.None
        || Warning is not null || Error is not null;
}

/// <summary>Which files of a processing preview a page of results lists.</summary>
public enum ProcessingPreviewFilter
{
    /// <summary>Every file with something to show (see <see cref="ProcessingPreviewItem.HasChanges"/>).</summary>
    Changes,

    /// <summary>Every file checked, unchanged ones included.</summary>
    All,

    /// <summary>Files that would be renamed.</summary>
    Renamed,

    /// <summary>Files whose tags would change.</summary>
    Tags,

    /// <summary>Files whose new name is taken.</summary>
    Clashes,

    /// <summary>Files processing would fail for.</summary>
    Errors
}

/// <summary>The counts of a processing preview, over every file it has checked so far.</summary>
/// <param name="Checked">Files checked.</param>
/// <param name="Changed">Files with anything to show.</param>
/// <param name="Renamed">Files that would be renamed.</param>
/// <param name="TagsChanged">Files whose tags would change.</param>
/// <param name="Clashes">Files whose new name is taken.</param>
/// <param name="Unchanged">Files processing would leave as they are.</param>
/// <param name="Errors">Files processing would fail for.</param>
public sealed record ProcessingPreviewSummary(
    int Checked,
    int Changed,
    int Renamed,
    int TagsChanged,
    int Clashes,
    int Unchanged,
    int Errors);

/// <summary>One page of a processing preview's results.</summary>
/// <param name="JobId">The preview's job.</param>
/// <param name="Status">The job's status, as the jobs routes word it (<c>running</c>, <c>completed</c>, ...).</param>
/// <param name="TotalFiles">The files the preview covers.</param>
/// <param name="Finished">True once every file is checked and clashes between them are known.</param>
/// <param name="Summary">The counts.</param>
/// <param name="Filter">Which files <paramref name="Items"/> is taken from.</param>
/// <param name="Offset">Where the page starts in the filtered list.</param>
/// <param name="Limit">The most items the page holds.</param>
/// <param name="Total">The files in the filtered list.</param>
/// <param name="Items">The page: sorted by series, then path.</param>
public sealed record ProcessingPreviewResults(
    Guid JobId,
    string Status,
    int TotalFiles,
    bool Finished,
    ProcessingPreviewSummary Summary,
    ProcessingPreviewFilter Filter,
    int Offset,
    int Limit,
    int Total,
    IReadOnlyList<ProcessingPreviewItem> Items);
