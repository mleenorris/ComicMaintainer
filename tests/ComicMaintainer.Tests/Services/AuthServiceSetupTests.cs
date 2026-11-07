using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// First-run setup (<see cref="AuthService.SetupAdminAsync"/>) against the real Identity stack: it is only available
/// on an instance without any account, never under Authelia, and requires the setup code (SEC-2).
/// </summary>
public sealed class AuthServiceSetupTests : IAsyncLifetime
{
    private const string Password = "Corr3ctHorse";
    private AuthTestHost _host = null!;

    public async Task InitializeAsync() => _host = await AuthTestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private string SetupCode => _host.Services.GetRequiredService<ISetupTokenProvider>().Token;

    [Fact]
    public async Task OnAFreshInstanceTheSetupCodeCreatesTheFirstAdministrator()
    {
        Assert.True(await _host.WithAuthServiceAsync(auth => auth.IsSetupRequiredAsync()));

        var (outcome, error) = await SetupAsync("owner", SetupCode);

        Assert.Equal(SetupOutcome.Created, outcome);
        Assert.Null(error);
        var owner = await _host.FindUserAsync("owner");
        Assert.NotNull(owner);
        Assert.Equal(["Admin"], await _host.InScopeAsync(s => s.GetRequiredService<UserManager<ApplicationUser>>().GetRolesAsync(owner!)));
        Assert.False(await _host.WithAuthServiceAsync(auth => auth.IsSetupRequiredAsync()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AAAAA-BBBBB-CCCCC-DDDDD")]
    public async Task WithoutTheRightSetupCodeNoAccountIsCreated(string? code)
    {
        var (outcome, error) = await SetupAsync("intruder", code);

        Assert.Equal(SetupOutcome.InvalidSetupToken, outcome);
        Assert.Equal(AuthService.InvalidSetupTokenError, error);
        Assert.Null(await _host.FindUserAsync("intruder"));
        Assert.True(await _host.WithAuthServiceAsync(auth => auth.IsSetupRequiredAsync()));
    }

    [Fact]
    public async Task TheGeneratedCodeCanBeRetypedInLowerCaseWithoutDashes()
    {
        var retyped = SetupCode.Replace("-", " ").ToLowerInvariant();

        Assert.Equal(SetupOutcome.Created, (await SetupAsync("owner", retyped)).Outcome);
    }

    // Setup used to be gated on "no user holds Admin", which Authelia's role sync could make true again.
    [Fact]
    public async Task OnceAnyAccountExistsSetupIsClosedEvenWithoutAnAdministrator()
    {
        await _host.CreateUserAsync("reader", Password, role: "ReadOnly");

        Assert.False(await _host.WithAuthServiceAsync(auth => auth.IsSetupRequiredAsync()));
        var (outcome, error) = await SetupAsync("intruder", SetupCode);

        Assert.Equal(SetupOutcome.NotAvailable, outcome);
        Assert.Equal(AuthService.SetupNotAvailableError, error);
        Assert.Null(await _host.FindUserAsync("intruder"));
    }

    [Fact]
    public async Task SetupCannotRunTwice()
    {
        Assert.Equal(SetupOutcome.Created, (await SetupAsync("owner", SetupCode)).Outcome);

        Assert.Equal(SetupOutcome.NotAvailable, (await SetupAsync("second", SetupCode)).Outcome);
        Assert.Null(await _host.FindUserAsync("second"));
    }

    [Fact]
    public async Task ConcurrentSetupRequestsCreateOnlyOneAdministrator()
    {
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => SetupAsync("owner" + i, SetupCode)));

        Assert.Single(attempts, attempt => attempt.Outcome == SetupOutcome.Created);
        Assert.Equal(1, await _host.InScopeAsync(s => Task.FromResult(s.GetRequiredService<UserManager<ApplicationUser>>().Users.Count())));
    }

    [Fact]
    public async Task APasswordIdentityRejectsIsReported()
    {
        var (outcome, error) = await _host.WithAuthServiceAsync(auth => auth.SetupAdminAsync("owner", "Admin123!", null, SetupCode));

        Assert.Equal(SetupOutcome.Rejected, outcome);
        Assert.Contains("published as an example", error);
        Assert.Null(await _host.FindUserAsync("owner"));
    }

    // Under Authelia its users and admin groups decide who administers the instance.
    [Fact]
    public async Task SetupIsNeverAvailableUnderAuthelia()
    {
        await using var authelia = await AuthTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["AutheliaSettings:Enabled"] = "true",
            ["AutheliaSettings:TrustedProxies"] = "172.18.0.0/16",
        });
        var code = authelia.Services.GetRequiredService<ISetupTokenProvider>().Token;

        Assert.False(await authelia.WithAuthServiceAsync(auth => auth.IsSetupRequiredAsync()));
        var (outcome, _) = await authelia.WithAuthServiceAsync(auth => auth.SetupAdminAsync("owner", Password, null, code));
        Assert.Equal(SetupOutcome.NotAvailable, outcome);
    }

    private Task<(SetupOutcome Outcome, string? Error)> SetupAsync(string username, string? code) =>
        _host.WithAuthServiceAsync(auth => auth.SetupAdminAsync(username, Password, null, code, "203.0.113.7"));
}
