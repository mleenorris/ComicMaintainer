namespace ComicMaintainer.Core.Data;

/// <summary>
/// Which ComicVine volume the issues of one series volume belong to: the files whose series
/// is <see cref="SeriesKey"/> and whose volume started in <see cref="StartYear"/>, or, when that
/// is not known, whose volume number is <see cref="VolumeNumber"/>; both are 0 for files that
/// know neither. A link with no <see cref="VolumeId"/> records that ComicVine has no such volume,
/// so it is not searched for again on every run.
/// </summary>
public class ComicVineVolumeLinkEntity
{
    public int Id { get; set; }

    /// <summary>The series name, in letters and digits only, lower case (see <see cref="Services.ComicVineIssueDetailsService.SeriesKey"/>).</summary>
    public string SeriesKey { get; set; } = string.Empty;

    /// <summary>The year the volume started, or 0 for files that do not know it.</summary>
    public int StartYear { get; set; }

    /// <summary>The volume number, for files that know it but not the start year; otherwise 0.</summary>
    public int VolumeNumber { get; set; }

    /// <summary>ComicVine's id of the volume; null when none was found.</summary>
    public int? VolumeId { get; set; }

    public string? VolumeName { get; set; }

    public string? Publisher { get; set; }

    public DateTime LinkedUtc { get; set; }

    /// <summary>When the volume's issues were last listed from ComicVine; null until they are.</summary>
    public DateTime? IssuesListedUtc { get; set; }
}

/// <summary>
/// One ComicVine issue of a linked volume. The volume's issue list gives the title, cover date
/// and summary; the issue's own page (fetched later, a request per issue) gives
/// <see cref="Writers"/> and <see cref="StoryArcsJson"/>.
/// </summary>
public class ComicVineIssueEntity
{
    /// <summary>ComicVine's issue id.</summary>
    public int Id { get; set; }

    public int VolumeId { get; set; }

    public string IssueNumber { get; set; } = string.Empty;

    public string? Name { get; set; }

    /// <summary>The cover date as ComicVine writes it: <c>2016-08-01</c>.</summary>
    public string? CoverDate { get; set; }

    public string? Summary { get; set; }

    public string? SiteUrl { get; set; }

    /// <summary>The cover at the largest size ComicVine has: what replaces an archive's cover.</summary>
    public string? CoverUrl { get; set; }

    /// <summary>The cover about a screen high, for the cover picker.</summary>
    public string? CoverThumbUrl { get; set; }

    /// <summary>The writers, comma-separated, once the issue's details are fetched.</summary>
    public string? Writers { get; set; }

    /// <summary>The story arcs the issue is part of, as JSON <c>[{"id":1,"name":"..."}]</c>.</summary>
    public string? StoryArcsJson { get; set; }

    public DateTime ListedUtc { get; set; }

    /// <summary>When the issue's details were fetched; null until they are.</summary>
    public DateTime? DetailsUtc { get; set; }
}

/// <summary>
/// The ComicVine issue a file was matched to, and when its details were last written into the
/// file. A file is written again only when the issue changes or gains details, so a run does
/// not open every archive it has already filled.
/// </summary>
public class ComicVineFileStateEntity
{
    public string FilePath { get; set; } = string.Empty;

    public int IssueId { get; set; }

    public DateTime AppliedUtc { get; set; }
}

/// <summary>A ComicVine story arc and its issues in ComicVine's order, fetched when first viewed.</summary>
public class ComicVineStoryArcEntity
{
    /// <summary>ComicVine's story arc id.</summary>
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public string? SiteUrl { get; set; }

    /// <summary>The arc's issues as JSON <c>[{"id","name","issue_number","volume"}]</c>, in order.</summary>
    public string IssuesJson { get; set; } = "[]";

    public DateTime FetchedUtc { get; set; }
}
