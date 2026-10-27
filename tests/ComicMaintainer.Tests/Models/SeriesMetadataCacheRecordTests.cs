using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Tests.Models;

public class SeriesMetadataCacheRecordTests
{
    [Theory]
    [InlineData("cover.jpg", "downloaded", true, false)]
    [InlineData("cover.jpg", "DOWNLOADED", true, false)]
    [InlineData("cover.jpg", "user", true, true)]
    [InlineData("cover.jpg", "User", true, true)]
    [InlineData("cover.jpg", "failed", false, false)]
    [InlineData("cover.jpg", null, false, false)]
    [InlineData(null, "downloaded", false, false)]
    [InlineData("", "user", false, true)]
    public void HasImage_RequiresALocalFileWithADownloadedOrUserStatus(
        string? localImageFile,
        string? imageStatus,
        bool expectedHasImage,
        bool expectedIsUserImage)
    {
        var record = new SeriesMetadataCacheRecord
        {
            LocalImageFile = localImageFile,
            ImageStatus = imageStatus
        };

        Assert.Equal(expectedHasImage, record.HasImage);
        Assert.Equal(expectedIsUserImage, record.IsUserImage);
    }
}
