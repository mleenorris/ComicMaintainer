namespace ComicMaintainer.Core.Models;

/// <summary>A ComicVine answer and the number of requests it took.</summary>
public sealed record ComicVineResult<T>(T Value, int Requests);

/// <summary>An issue as a volume's issue list gives it, with its cover's full-size and screen-size images.</summary>
public sealed record ComicVineIssueSummary(
    int Id,
    string IssueNumber,
    string? Name,
    string? CoverDate,
    string? Summary,
    string? SiteUrl,
    string? CoverUrl = null,
    string? CoverThumbUrl = null);

/// <summary>A story arc an issue is part of.</summary>
public sealed record ComicVineArcReference(int Id, string Name);

/// <summary>An issue's writers and story arcs, from the issue's own page.</summary>
public sealed record ComicVineIssueDetails(
    int Id,
    IReadOnlyList<string> Writers,
    IReadOnlyList<ComicVineArcReference> StoryArcs);

/// <summary>An issue of a story arc, as the arc lists it.</summary>
public sealed record ComicVineArcIssue(int Id, string? Name, string? IssueNumber, string? Volume);

/// <summary>A story arc and its issues, in ComicVine's order.</summary>
public sealed record ComicVineStoryArc(
    int Id,
    string Name,
    string? Summary,
    string? SiteUrl,
    IReadOnlyList<ComicVineArcIssue> Issues);

/// <summary>What one run of fetching issue details from ComicVine did.</summary>
/// <param name="Files">Files looked at.</param>
/// <param name="Filled">Files whose tags got ComicVine's details.</param>
/// <param name="Unchanged">Files matched to a ComicVine issue that needed nothing new.</param>
/// <param name="NoVolume">Files whose series volume is not on ComicVine, or not known.</param>
/// <param name="NoIssue">Files whose volume ComicVine has but not their issue number.</param>
/// <param name="Skipped">Duplicates, Annuals and Specials, and files without a series or number.</param>
/// <param name="Failed">Files that could not be written or looked up.</param>
/// <param name="Requests">ComicVine requests made.</param>
/// <param name="StoppedEarly">True when the run used up its requests, or ComicVine stopped answering, before every file was done.</param>
public sealed record ComicVineDetailsRunSummary(
    int Files,
    int Filled,
    int Unchanged,
    int NoVolume,
    int NoIssue,
    int Skipped,
    int Failed,
    int Requests,
    bool StoppedEarly)
{
    public override string ToString()
        => $"ComicVine issue details: {Filled} files filled, {Unchanged} unchanged, {NoVolume} without a ComicVine volume, " +
           $"{NoIssue} without a matching issue, {Skipped} skipped, {Failed} failed; {Requests} requests" +
           (StoppedEarly ? "; the rest continue next run" : string.Empty) + ".";
}

/// <summary>
/// One volume of a series as the library holds it, and the ComicVine volume it is linked to:
/// <see cref="VolumeNumber"/> and <see cref="StartYear"/> are what its files say (0 when they do
/// not say), <see cref="Issues"/> how many regular issues it has. The ComicVine fields are null
/// when the volume is not linked yet, or ComicVine has no such volume.
/// </summary>
public sealed record SeriesVolumeLink(
    int VolumeNumber,
    int StartYear,
    int Issues,
    int? ComicVineVolumeId,
    string? Name,
    string? Publisher,
    string? SiteUrl);

/// <summary>
/// The ComicVine page of the file at <see cref="FilePath"/>: its issue's page when the issue is
/// known (<see cref="IssueUrl"/> is null when ComicVine gave none), and always its volume's page.
/// <see cref="Filled"/> is true when Fetch Issue Details wrote the file from that issue, false when
/// the issue was matched by number in the volume's stored listing, or not matched at all
/// (<see cref="IssueId"/> null).
/// </summary>
public sealed record IssueComicVineLink(
    string FilePath,
    int? IssueId,
    string? IssueNumber,
    string? IssueName,
    string? IssueUrl,
    int VolumeId,
    string? VolumeName,
    string VolumeUrl,
    bool Filled);

/// <summary>
/// One ComicVine issue read by its id: its listing fields, the volume it belongs to and its
/// details, as one request gives them.
/// </summary>
public sealed record ComicVineIssue(ComicVineIssueSummary Summary, int VolumeId, string? VolumeName, ComicVineIssueDetails Details);

/// <summary>
/// ComicVine's volumes of one exact name, as listed by name (see
/// <see cref="Interfaces.IComicVineVolumeSearch.ListVolumesNamedAsync"/>): <see cref="Complete"/> is
/// false when the request limit cut the list off, so later volumes may be missing.
/// </summary>
public sealed record ComicVineVolumeList(IReadOnlyList<ExternalSeriesMetadata> Volumes, int Requests, bool Complete);

/// <summary>A story arc some issues of a series are part of.</summary>
public sealed record SeriesStoryArc(int Id, string Name, IReadOnlyList<string> FilePaths);

/// <summary>An issue of a story arc, and the library file that holds it, if any.</summary>
public sealed record StoryArcEntry(int Id, string? Name, string? IssueNumber, string? Volume, string? FilePath);

/// <summary>A story arc's issues in reading order, as ComicVine lists them.</summary>
public sealed record StoryArcReadingOrder(
    int Id,
    string Name,
    string? Summary,
    string? SiteUrl,
    IReadOnlyList<StoryArcEntry> Issues);

/// <summary>A cover ComicVine has for an issue of the file's volume.</summary>
public sealed record CoverCandidate(
    int IssueId,
    string IssueNumber,
    string? Name,
    string? CoverDate,
    string CoverUrl,
    string? ThumbUrl,
    string? SiteUrl);

/// <summary>
/// The covers a file's cover can be replaced with: those of every issue of its ComicVine volume
/// that has one, in issue order, with <see cref="MatchedIssueId"/> the issue the file was matched
/// to. <see cref="Reason"/> says why there is no match, or no cover for it.
/// </summary>
public sealed record CoverCandidates(
    int? VolumeId,
    string? VolumeName,
    int? MatchedIssueId,
    string? Reason,
    IReadOnlyList<CoverCandidate> Covers);

/// <summary>What replacing a file's cover with a ComicVine cover did; <see cref="Error"/> says why it failed.</summary>
public sealed record IssueCoverReplaceResult(bool Succeeded, bool Changed, string? Error);

/// <summary>
/// A ComicVine cover fetched for the cover picker: the image, or, when <see cref="Bytes"/> is
/// null, <see cref="Error"/> says why it could not be fetched.
/// </summary>
public sealed record IssueCoverImage(byte[]? Bytes, string? ContentType, string? Error);
