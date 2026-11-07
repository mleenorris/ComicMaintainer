namespace ComicMaintainer.Core.Models;

/// <summary>
/// Helpers for <see cref="ComicMetadataFieldFlags"/>, the mask that says which
/// <see cref="ComicMetadata"/> fields a metadata write or edit applies. A field outside the mask
/// is left exactly as it is; a field inside it is set to the supplied value, and an empty value
/// removes it.
/// </summary>
public static class ComicMetadataFields
{
    /// <summary>Every field the application models.</summary>
    public const ComicMetadataFieldFlags All =
        ComicMetadataFieldFlags.Series |
        ComicMetadataFieldFlags.Title |
        ComicMetadataFieldFlags.Issue |
        ComicMetadataFieldFlags.Volume |
        ComicMetadataFieldFlags.Publisher |
        ComicMetadataFieldFlags.Year |
        ComicMetadataFieldFlags.Summary |
        ComicMetadataFieldFlags.Authors |
        ComicMetadataFieldFlags.Tags;

    /// <summary>
    /// The fields <paramref name="metadata"/> carries a value for: non-null strings and year,
    /// non-empty lists. Callers that send no explicit field list get exactly these applied, so an
    /// omitted field is never cleared.
    /// </summary>
    public static ComicMetadataFieldFlags Supplied(ComicMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var flags = ComicMetadataFieldFlags.None;
        if (metadata.Series is not null) flags |= ComicMetadataFieldFlags.Series;
        if (metadata.Title is not null) flags |= ComicMetadataFieldFlags.Title;
        if (metadata.Issue is not null) flags |= ComicMetadataFieldFlags.Issue;
        if (metadata.Volume is not null) flags |= ComicMetadataFieldFlags.Volume;
        if (metadata.Publisher is not null) flags |= ComicMetadataFieldFlags.Publisher;
        if (metadata.Year.HasValue) flags |= ComicMetadataFieldFlags.Year;
        if (metadata.Summary is not null) flags |= ComicMetadataFieldFlags.Summary;
        if (metadata.Authors.Count > 0) flags |= ComicMetadataFieldFlags.Authors;
        if (metadata.Tags.Count > 0) flags |= ComicMetadataFieldFlags.Tags;
        return flags;
    }

    /// <summary>
    /// Maps an API field name ("series", "writer", "number", ...) to its flag, or
    /// <see cref="ComicMetadataFieldFlags.None"/> for an unknown name.
    /// </summary>
    public static ComicMetadataFieldFlags Parse(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return ComicMetadataFieldFlags.None;
        return field.Trim().ToLowerInvariant() switch
        {
            "series" => ComicMetadataFieldFlags.Series,
            "title" => ComicMetadataFieldFlags.Title,
            "issue" or "number" => ComicMetadataFieldFlags.Issue,
            "volume" => ComicMetadataFieldFlags.Volume,
            "publisher" => ComicMetadataFieldFlags.Publisher,
            "year" => ComicMetadataFieldFlags.Year,
            "summary" or "description" => ComicMetadataFieldFlags.Summary,
            "authors" or "writer" or "writers" => ComicMetadataFieldFlags.Authors,
            "tags" => ComicMetadataFieldFlags.Tags,
            _ => ComicMetadataFieldFlags.None
        };
    }

    /// <summary>
    /// Combines a list of API field names into a mask. Returns false, with the offending name in
    /// <paramref name="unknownField"/>, when a name is not a known field.
    /// </summary>
    public static bool TryParse(IEnumerable<string?> fields, out ComicMetadataFieldFlags flags, out string? unknownField)
    {
        ArgumentNullException.ThrowIfNull(fields);
        flags = ComicMetadataFieldFlags.None;
        unknownField = null;
        foreach (var field in fields)
        {
            var flag = Parse(field);
            if (flag == ComicMetadataFieldFlags.None)
            {
                unknownField = field ?? string.Empty;
                flags = ComicMetadataFieldFlags.None;
                return false;
            }

            flags |= flag;
        }

        return true;
    }

    /// <summary>
    /// Copies the fields in <paramref name="fields"/> from <paramref name="source"/> onto
    /// <paramref name="target"/>, including empty values, and leaves every other field alone.
    /// </summary>
    public static void Copy(ComicMetadata source, ComicMetadata target, ComicMetadataFieldFlags fields)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (fields.HasFlag(ComicMetadataFieldFlags.Series)) target.Series = source.Series;
        if (fields.HasFlag(ComicMetadataFieldFlags.Title)) target.Title = source.Title;
        if (fields.HasFlag(ComicMetadataFieldFlags.Issue)) target.Issue = source.Issue;
        if (fields.HasFlag(ComicMetadataFieldFlags.Volume)) target.Volume = source.Volume;
        if (fields.HasFlag(ComicMetadataFieldFlags.Publisher)) target.Publisher = source.Publisher;
        if (fields.HasFlag(ComicMetadataFieldFlags.Year)) target.Year = source.Year;
        if (fields.HasFlag(ComicMetadataFieldFlags.Summary)) target.Summary = source.Summary;
        if (fields.HasFlag(ComicMetadataFieldFlags.Authors)) target.Authors = new List<string>(source.Authors);
        if (fields.HasFlag(ComicMetadataFieldFlags.Tags)) target.Tags = new List<string>(source.Tags);
    }
}
