namespace ComicMaintainer.Core.Notifications;

/// <summary>The targets a notification can go to; one of each can be configured.</summary>
public static class NotificationTarget
{
    public const string Ntfy = "ntfy";
    public const string Gotify = "gotify";
    public const string Discord = "discord";
    public const string Webhook = "webhook";
    public const string Komga = "komga";
    public const string Kavita = "kavita";

    /// <summary>Every target, in the order the Settings page lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Ntfy, Gotify, Discord, Webhook, Komga, Kavita];

    /// <summary>
    /// True for the media servers that are asked to rescan the library instead of being sent a
    /// message.
    /// </summary>
    public static bool IsRescan(string target) => target is Komga or Kavita;

    /// <summary>The name to show for <paramref name="target"/>.</summary>
    public static string DisplayName(string target) => target switch
    {
        Ntfy => "ntfy",
        Gotify => "Gotify",
        Discord => "Discord",
        Webhook => "the JSON webhook",
        Komga => "Komga",
        Kavita => "Kavita",
        _ => target,
    };
}

/// <summary>The events a target can choose to receive.</summary>
public static class NotificationEventType
{
    /// <summary>New chapters arrived; messages are batched per series.</summary>
    public const string NewChapters = "new-chapters";

    /// <summary>A Suwayomi import pass could not bring every chapter in.</summary>
    public const string SuwayomiImportFailed = "suwayomi-import-failed";

    /// <summary>A scheduled job failed.</summary>
    public const string JobFailed = "job-failed";

    /// <summary>A scheduled job finished and reported something to look at.</summary>
    public const string JobFindings = "job-findings";

    /// <summary>Files in the library changed; rescan targets only.</summary>
    public const string LibraryChanged = "library-changed";

    /// <summary>A target just accepted a test. For the queue itself, so no target can choose it.</summary>
    internal const string TargetVerified = "target-verified";

    /// <summary>The events a target that is sent messages can receive.</summary>
    public static IReadOnlyList<string> PushEvents { get; } = [NewChapters, SuwayomiImportFailed, JobFailed, JobFindings];

    /// <summary>The events that make a rescan target scan.</summary>
    public static IReadOnlyList<string> RescanEvents { get; } = [NewChapters, LibraryChanged];

    /// <summary>What a target that is sent messages receives until it is configured otherwise.</summary>
    public const string DefaultPushEvents = "new-chapters,suwayomi-import-failed,job-failed,job-findings";

    /// <summary>What a rescan target scans for until it is configured otherwise.</summary>
    public const string DefaultRescanEvents = "new-chapters,library-changed";

    /// <summary>The events <paramref name="target"/> can receive.</summary>
    public static IReadOnlyList<string> AllowedFor(string target) =>
        NotificationTarget.IsRescan(target) ? RescanEvents : PushEvents;

    /// <summary>
    /// The comma-separated events in <paramref name="configured"/> that <paramref name="target"/>
    /// can receive. Anything else is ignored, so a typo in an environment variable turns off one
    /// event instead of the target.
    /// </summary>
    public static IReadOnlySet<string> Parse(string target, string? configured)
    {
        var allowed = AllowedFor(target);
        return Split(configured).Where(allowed.Contains).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The trimmed, non-blank entries of a comma-separated event list.</summary>
    public static IEnumerable<string> Split(string? configured) =>
        (configured ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant());
}
