using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services;

public class SeriesDisplayTitleResolverCoverageTests
{
    [Fact]
    public void Resolve_NeverReturnsNullWhenTheCanonicalTitleIsMissing()
    {
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "key",
            CanonicalTitle = null!,
            PreferredLanguage = "ja",
            LocalizedTitles = new List<LocalizedTitle> { new("One Piece", "en") }
        };

        Assert.Equal(string.Empty, SeriesDisplayTitleResolver.Resolve(record, globalDefaultLanguage: null));
    }
}
