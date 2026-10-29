using System.Security.Claims;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.Http;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class HttpUserContextAccessorTests
{
    private static HttpUserContextAccessor Create(HttpContext? context) =>
        new(new StubHttpContextAccessor { HttpContext = context });

    private static ClaimsPrincipal Principal(string? authenticationType, params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType));

    [Fact]
    public void UserId_OutsideARequest_IsNull()
    {
        Assert.Null(Create(null).UserId);
    }

    [Fact]
    public void UserId_WhenRequestUserIsNull_IsNull()
    {
        // DefaultHttpContext never hands out a null User, but other HttpContext implementations may.
        var context = new Mock<HttpContext>();
        context.SetupGet(c => c.User).Returns(default(ClaimsPrincipal)!);

        Assert.Null(Create(context.Object).UserId);
    }

    [Fact]
    public void UserId_WhenUserHasNoIdentity_IsNull()
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal() };

        Assert.Null(Create(context).UserId);
    }

    [Fact]
    public void UserId_ForAnonymousUser_IsNull()
    {
        var context = new DefaultHttpContext
        {
            User = Principal(authenticationType: null, new Claim(ClaimTypes.NameIdentifier, "user-1"))
        };

        Assert.Null(Create(context).UserId);
    }

    [Fact]
    public void UserId_ForAuthenticatedUserWithoutNameIdentifier_IsNull()
    {
        var context = new DefaultHttpContext
        {
            User = Principal("Test", new Claim(ClaimTypes.Name, "alice"))
        };

        Assert.Null(Create(context).UserId);
    }

    [Fact]
    public void UserId_ForAuthenticatedUserWithEmptyNameIdentifier_IsNull()
    {
        var context = new DefaultHttpContext
        {
            User = Principal("Test", new Claim(ClaimTypes.NameIdentifier, string.Empty))
        };

        Assert.Null(Create(context).UserId);
    }

    [Fact]
    public void UserId_ForAuthenticatedUser_ReturnsNameIdentifier()
    {
        var context = new DefaultHttpContext
        {
            User = Principal("Test", new Claim(ClaimTypes.Name, "alice"), new Claim(ClaimTypes.NameIdentifier, "user-42"))
        };

        Assert.Equal("user-42", Create(context).UserId);
    }

    [Fact]
    public void UserId_ReflectsTheCurrentContextOnEachRead()
    {
        var accessor = new StubHttpContextAccessor();
        var userContext = new HttpUserContextAccessor(accessor);

        accessor.HttpContext = new DefaultHttpContext
        {
            User = Principal("Test", new Claim(ClaimTypes.NameIdentifier, "first"))
        };
        Assert.Equal("first", userContext.UserId);

        accessor.HttpContext = new DefaultHttpContext
        {
            User = Principal("Test", new Claim(ClaimTypes.NameIdentifier, "second"))
        };
        Assert.Equal("second", userContext.UserId);

        accessor.HttpContext = null;
        Assert.Null(userContext.UserId);
    }

    // A plain holder rather than HttpContextAccessor, whose AsyncLocal storage is static and could leak between tests.
    private sealed class StubHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
