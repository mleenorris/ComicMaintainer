using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Tests.Models;

public class FileDtoCoverageTests
{
    [Fact]
    public void FromComicFile_ConvertsALocalTimestampToTheSameUnixInstant()
    {
        var instant = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var file = new ComicFile
        {
            FilePath = "/comics/a.cbz",
            FileName = "a.cbz",
            LastModified = instant.ToLocalTime(),
            IsRenamed = true,
            IsNormalized = true,
            IsRead = true
        };

        var dto = FileDto.FromComicFile(file);

        Assert.Equal(new DateTimeOffset(instant).ToUnixTimeSeconds(), dto.Modified);
        Assert.True(dto.Renamed);
        Assert.True(dto.Normalized);
        Assert.True(dto.Read);
        Assert.False(dto.Processed);
        Assert.False(dto.Duplicate);
    }
}
