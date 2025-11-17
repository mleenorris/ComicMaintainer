using ComicMaintainer.Core.Models.Auth;

namespace ComicMaintainer.Tests.Models.Auth;

public class ApplicationRoleTests
{
    [Fact]
    public void ApplicationRole_CanBeCreated()
    {
        // Act
        var role = new ApplicationRole();

        // Assert
        Assert.NotNull(role);
    }

    [Fact]
    public void ApplicationRole_PropertiesCanBeSet()
    {
        // Act
        var role = new ApplicationRole
        {
            Id = "role-1",
            Name = "Admin",
            NormalizedName = "ADMIN",
            Description = "Administrator role",
            ConcurrencyStamp = "stamp-123"
        };

        // Assert
        Assert.Equal("role-1", role.Id);
        Assert.Equal("Admin", role.Name);
        Assert.Equal("ADMIN", role.NormalizedName);
        Assert.Equal("Administrator role", role.Description);
        Assert.Equal("stamp-123", role.ConcurrencyStamp);
    }

    [Fact]
    public void ApplicationRole_DescriptionCanBeNull()
    {
        // Act
        var role = new ApplicationRole
        {
            Name = "User"
        };

        // Assert
        Assert.Null(role.Description);
    }
}

public class ApplicationUserTests
{
    [Fact]
    public void ApplicationUser_PropertiesCanBeSet()
    {
        // Act
        var user = new ApplicationUser
        {
            Id = "user-1",
            UserName = "testuser",
            Email = "test@example.com",
            FullName = "Test User",
            IsActive = true,
            ApiKey = "test-api-key"
        };

        // Assert
        Assert.Equal("user-1", user.Id);
        Assert.Equal("testuser", user.UserName);
        Assert.Equal("test@example.com", user.Email);
        Assert.Equal("Test User", user.FullName);
        Assert.True(user.IsActive);
        Assert.Equal("test-api-key", user.ApiKey);
    }

    [Fact]
    public void ApplicationUser_FullNameCanBeNull()
    {
        // Act
        var user = new ApplicationUser();

        // Assert
        Assert.Null(user.FullName);
    }

    [Fact]
    public void ApplicationUser_ApiKeyCanBeNull()
    {
        // Act
        var user = new ApplicationUser();

        // Assert
        Assert.Null(user.ApiKey);
    }
}
