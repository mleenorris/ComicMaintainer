using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.WebApi.Authentication;
using ComicMaintainer.WebApi.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Serilog;

namespace ComicMaintainer.WebApi.Startup;

/// <summary>
/// Registers Identity, JWT/Authelia authentication and the authorization policies.
/// </summary>
internal static class AuthServiceCollectionExtensions
{
    /// <summary>The environment variable that sets the first-run setup code; <c>SETUP_TOKEN_FILE</c> names a file that holds it.</summary>
    public const string SetupTokenEnvironmentVariable = "SETUP_TOKEN";

    public static IServiceCollection AddComicMaintainerAuth(this IServiceCollection services, IConfiguration configuration)
    {
        // Configure Identity
        services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
        {
            // Password settings
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequiredLength = 8;

            // Lockout: five wrong passwords in a row lock the account for 15 minutes (AuthService.LoginAsync).
            // New accounts have lockout enabled; AuthService enables it on older accounts at their first failure.
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            
            // User settings
            options.User.RequireUniqueEmail = true;
        })
        .AddEntityFrameworkStores<ComicMaintainerDbContext>()
        .AddPasswordValidator<PublishedPasswordValidator>()
        .AddDefaultTokenProviders();

        // Read settings through the same binder as IOptions<JwtSettings>, then resolve the signing secret once so
        // token validation and AuthService use the same value. Program logs where it came from once logging is set up.
        var jwtSettings = JwtSettingsBinder.Load(configuration);
        var jwtSecret = JwtSecretResolver.Resolve(configuration);
        services.AddSingleton(jwtSecret);
        services.PostConfigure<JwtSettings>(options => options.Secret = jwtSecret.Value);

        // The one-time code first-run setup requires (see SetupTokenProvider): SETUP_TOKEN, or the file
        // SETUP_TOKEN_FILE names, or a random code generated for this run and logged while setup is available.
        services.AddSingleton<ISetupTokenProvider>(new SetupTokenProvider(SecretEnvironmentVariable.Read(SetupTokenEnvironmentVariable, out _)));

        // Check if Authelia is enabled. Read through the same binder as IOptions<AutheliaSettings> (used by the
        // handler), so the validation, schemes and CanAdminister fallback below match what the handler sees.
        var autheliaSettings = AutheliaSettingsBinder.Load(configuration);

        // Anyone who can reach the app directly could forge the Authelia headers, so the reverse proxies
        // allowed to send them must be configured; refuse to start otherwise.
        if (autheliaSettings.Enabled)
        {
            if (!TrustedProxyAllowlist.TryParse(autheliaSettings.TrustedProxies, out var trustedProxies, out var error))
            {
                throw new InvalidOperationException(error);
            }

            Log.Information("Authelia headers will only be accepted from {TrustedProxies}", string.Join(", ", trustedProxies.Networks));
        }

        // Configure authentication schemes
        var authBuilder = services.AddAuthentication(options =>
        {
            // Set Authelia as default if enabled, otherwise use JWT
            if (autheliaSettings.Enabled)
            {
                options.DefaultAuthenticateScheme = "Authelia";
                options.DefaultChallengeScheme = "Authelia";
                Log.Information("✅ Authelia authentication enabled - forward auth headers will be trusted");
            }
            else
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            }
        });

        // Add Authelia authentication handler if enabled
        if (autheliaSettings.Enabled)
        {
            authBuilder.AddScheme<AutheliaAuthenticationOptions, AutheliaAuthenticationHandler>(
                "Authelia",
                options => { });
            
            Log.Information("Authelia authentication configured with fallback to JWT for SSE connections");
        }

        // Session tokens: signed in with a password (or minted for a password change), sent in the Authorization
        // header. Never read from the query string: proxies log URLs. Every request checks that the session has not
        // ended (IAuthSessionValidator): sign-out, password or role changes and disabled or deleted accounts revoke.
        services.AddSingleton<IAuthSessionValidator, AuthSessionValidator>();
        authBuilder.AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtSettings.Issuer,
                ValidAudience = jwtSettings.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret.Value))
            };

            // JSON 401/403 bodies instead of redirects, and the per-request session check.
            options.Events = JwtSessionEvents.SessionTokens();
        });

        // Stream tokens: one-minute, single-use tokens with their own audience, accepted only in the query string of
        // the event stream, the one endpoint EventSource cannot send a header to (see StreamTokenAuthentication).
        authBuilder.AddJwtBearer(StreamTokenAuthentication.SchemeName, options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                RequireExpirationTime = true,
                ValidIssuer = jwtSettings.Issuer,
                ValidAudience = AuthTokens.StreamAudience(jwtSettings.Audience),
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret.Value)),
                // The default five minutes of clock skew would let a one-minute token live six.
                ClockSkew = TimeSpan.FromSeconds(5)
            };

            options.Events = JwtSessionEvents.StreamTokens();
        });

        // Configure authorization policies to support multiple authentication schemes
        services.AddAuthorization(options =>
        {
            // Default policy that accepts both Authelia and JWT authentication
            // This allows SSE connections to work with JWT tokens even when Authelia is the default
            if (autheliaSettings.Enabled)
            {
                options.DefaultPolicy = new AuthorizationPolicyBuilder()
                    .AddAuthenticationSchemes("Authelia", JwtBearerDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .Build();
                
                Log.Information("Authorization policy configured to accept both Authelia and JWT Bearer tokens");
            }
            else
            {
                options.DefaultPolicy = new AuthorizationPolicyBuilder()
                    .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .Build();
            }

            // Schemes accepted by every named policy, mirroring the default policy so
            // SSE/EventSource JWT connections keep working under Authelia.
            var schemes = autheliaSettings.Enabled
                ? new[] { "Authelia", JwtBearerDefaults.AuthenticationScheme }
                : new[] { JwtBearerDefaults.AuthenticationScheme };

            // Library mutations: allowed for any authenticated user except members of
            // the ReadOnly role. Denying by role (rather than requiring User/Admin)
            // keeps existing deployments working even if a user has no role assigned.
            options.AddPolicy(AuthorizationPolicies.CanModifyLibrary, policy => policy
                .AddAuthenticationSchemes(schemes)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => !context.User.IsInRole("ReadOnly")));

            // Administration: requires the Admin role.
            //
            // Compatibility note: when Authelia is the identity provider but no admin
            // groups are configured, no user can ever be mapped to the Admin role. In
            // that configuration ComicMaintainer cannot distinguish administrators, so
            // any non-ReadOnly user is treated as one rather than locking everybody out
            // of the settings screen.
            var autheliaHasAdminGroups = autheliaSettings.Enabled
                && !string.IsNullOrWhiteSpace(autheliaSettings.AdminGroups);
            var autheliaAdminFallback = autheliaSettings.Enabled && !autheliaHasAdminGroups;

            options.AddPolicy(AuthorizationPolicies.CanAdminister, policy => policy
                .AddAuthenticationSchemes(schemes)
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    context.User.IsInRole("Admin")
                    || (autheliaAdminFallback && !context.User.IsInRole("ReadOnly"))));
        });

        return services;
    }
}
