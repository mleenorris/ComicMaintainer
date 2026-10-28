using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models.Auth;
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
            
            // User settings
            options.User.RequireUniqueEmail = true;
        })
        .AddEntityFrameworkStores<ComicMaintainerDbContext>()
        .AddDefaultTokenProviders();

        // Configure JWT Authentication. Read through the same binder as IOptions<JwtSettings> (which AuthService signs
        // tokens with), so tokens are validated with the secret they were signed with.
        var jwtSettings = JwtSettingsBinder.Load(configuration);
        var jwtSecret = jwtSettings.Secret;
        var defaultSecret = "YourSecretKeyHere-ChangeInProduction-MustBeAtLeast32CharactersLong!";

        // A blank JWT_SECRET counts as unset, but it was meant to hold the secret (such as `$(cat jwt_secret.txt)` with
        // the file missing), so it must not fall back to the public placeholder shipped in appsettings.json.
        if (JwtSettingsBinder.IsSecretVariableBlank() && (string.IsNullOrWhiteSpace(jwtSecret) || jwtSecret == defaultSecret))
        {
            throw new InvalidOperationException(
                "JWT_SECRET is set but blank, and JwtSettings:Secret is missing or blank too (the placeholder shipped in " +
                "appsettings.json does not count). Set JWT_SECRET to a random secret of at least 32 characters, e.g. the " +
                "output of `openssl rand -base64 48`.");
        }

        if (string.IsNullOrWhiteSpace(jwtSecret))
        {
            throw new InvalidOperationException("JWT_SECRET must be configured in appsettings.json or environment variable");
        }

        // Warn if using default JWT secret
        if (jwtSecret == defaultSecret)
        {
            Log.Warning("⚠️ WARNING: Using default JWT secret! Please set JWT_SECRET environment variable for production.");
            Log.Warning("⚠️ Current secret is stored in appsettings.json which is not persisted in /Config directory.");
            Log.Warning("⚠️ Set JWT_SECRET environment variable to use a secure, persistent secret.");
        }

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

        // Always add JWT Bearer for backward compatibility and API access
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
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
            };
            
            // Configure events to return JSON for authentication failures and support token in query string
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    // Allow token to be passed via query string for EventSource/SSE connections
                    // EventSource API doesn't support custom headers, so we need this for SSE endpoints
                    if (string.IsNullOrEmpty(context.Token) && context.Request.Query.ContainsKey("access_token"))
                    {
                        context.Token = context.Request.Query["access_token"];
                    }
                    return Task.CompletedTask;
                },
                OnChallenge = context =>
                {
                    // Override the default behavior to return JSON instead of redirecting
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    
                    var result = JsonSerializer.Serialize(new { error = "Unauthorized", message = "Authentication required" });
                    return context.Response.WriteAsync(result);
                },
                OnForbidden = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    
                    var result = JsonSerializer.Serialize(new { error = "Forbidden", message = "Insufficient permissions" });
                    return context.Response.WriteAsync(result);
                }
            };
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
