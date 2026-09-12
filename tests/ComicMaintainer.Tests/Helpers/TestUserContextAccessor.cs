using ComicMaintainer.Core.Interfaces;

namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// Test double for <see cref="IUserContextAccessor"/> with a settable current user, so a
/// test can switch identity mid-run to assert that per-user state stays isolated.
/// </summary>
public class TestUserContextAccessor : IUserContextAccessor
{
    public TestUserContextAccessor(string? userId = "test-user")
    {
        UserId = userId;
    }

    public string? UserId { get; set; }
}
