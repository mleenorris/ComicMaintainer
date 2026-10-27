using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace ComicMaintainer.WebApi.Infrastructure;

/// <summary>
/// Conditional-request evaluation shared by the endpoints that compute their
/// own entity tags (file covers and series images), so they answer
/// revalidation requests the same way.
/// </summary>
public static class ConditionalRequests
{
    /// <summary>
    /// Evaluates an <c>If-None-Match</c> header against <paramref name="etag"/>
    /// using the weak comparison mandated by RFC 9110 §13.1.2. Handles
    /// <c>*</c>, comma-separated lists, multiple header values and
    /// <c>W/</c>-prefixed tags. True means the client's copy is current, so a
    /// GET should be answered with 304 Not Modified.
    /// </summary>
    /// <param name="ifNoneMatch">The request's <c>If-None-Match</c> header values.</param>
    /// <param name="etag">The current representation's strong entity tag, quotes included.</param>
    public static bool IfNoneMatchSatisfied(StringValues ifNoneMatch, string etag)
    {
        if (StringValues.IsNullOrEmpty(ifNoneMatch))
        {
            return false;
        }
        if (!EntityTagHeaderValue.TryParseList(ifNoneMatch, out var candidates))
        {
            return false;
        }
        var current = new EntityTagHeaderValue(etag);
        foreach (var candidate in candidates)
        {
            if (candidate.Tag.Equals("*", StringComparison.Ordinal)
                || candidate.Compare(current, useStrongComparison: false))
            {
                return true;
            }
        }
        return false;
    }
}
