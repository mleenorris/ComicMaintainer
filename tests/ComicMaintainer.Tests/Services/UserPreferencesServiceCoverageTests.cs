using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class UserPreferencesServiceCoverageTests
{
    [Fact]
    public async Task SavePreferencesAsync_RejectsANullUpdateWithoutTouchingTheDatabase()
    {
        var factory = new Mock<IDbContextFactory<ComicMaintainerDbContext>>(MockBehavior.Strict);
        var service = new UserPreferencesService(factory.Object, NullLogger<UserPreferencesService>.Instance);

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => service.SavePreferencesAsync(null!));

        Assert.Equal("update", ex.ParamName);
        factory.VerifyNoOtherCalls();
    }
}
