using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc.Controllers;
using DocuMind.Services.Accounts;
using DocuMind.Services.Email;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocuMind.Api.Configuration;

/// <summary>Keeps account wiring and startup validation separate from the HTTP controller.</summary>
public static class AccountConfiguration
{
    /// <summary>Registers account use cases, validates their settings, and selects safe email delivery.</summary>
    public static IServiceCollection AddDocuMindAccounts(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<AccountOptions>().Bind(configuration.GetSection(AccountOptions.SectionName))
            .Validate(options => IsTrustedApplicationUrl(options.ApplicationUrl, environment.IsDevelopment()),
                "Accounts:ApplicationUrl must be an absolute HTTP(S) URL without credentials, query, or fragment; HTTPS is required outside Development.")
            .Validate(options => options.ConfirmationTokenLifetime >= TimeSpan.FromSeconds(1)
                && options.ConfirmationTokenLifetime <= TimeSpan.FromDays(7),
                "Accounts:ConfirmationTokenLifetime must be between one second and seven days.")
            .Validate(options => options.ResendCooldown >= TimeSpan.FromSeconds(1)
                && options.ResendCooldown <= TimeSpan.FromDays(1),
                "Accounts:ResendCooldown must be between one second and one day.")
            .ValidateOnStart();
        services.AddOptions<EmailConfirmationTokenOptions>().Configure<Microsoft.Extensions.Options.IOptions<AccountOptions>>(
            (tokens, accounts) => tokens.TokenLifespan = accounts.Value.ConfirmationTokenLifetime);
        services.AddMemoryCache();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IIdentitySessionService, IdentitySessionService>();

        if (environment.IsDevelopment())
        {
            services.TryAddSingleton<IEmailSender, DevelopmentPreviewEmailSender>();
        }
        else if (!services.Any(service => service.ServiceType == typeof(IEmailSender)))
        {
            // A real provider must be explicitly registered before this method in production.
            // Never silently use previews or a no-op sender when users need real confirmation mail.
            throw new InvalidOperationException(
                "No production email sender is configured. Register a real DocuMind.Services.Email.IEmailSender before AddDocuMindAccounts. Development previews are disabled outside Development.");
        }

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("account-write", context =>
            {
                // Canonical action names prevent casing or trailing-slash variations from bypassing a limit.
                var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ActionName
                    ?? "account-write";
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"{action}:{context.Connection.RemoteIpAddress}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                    });
            });
            options.AddPolicy("account-confirm", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
            // Session writes have their own budget; canonical action names also protect route variations.
            options.AddPolicy("account-session", context => RateLimitPartition.GetFixedWindowLimiter(
                $"{context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ActionName}:{context.Connection.RemoteIpAddress}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
            options.AddPolicy("account-bootstrap", context => RateLimitPartition.GetFixedWindowLimiter(
                $"{context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ActionName}:{context.Connection.RemoteIpAddress}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
            options.OnRejected = async (context, cancellationToken) =>
            {
                // No queued requests: callers get immediate feedback, plus a conservative retry window.
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    message = "Too many account requests. Try again in a minute."
                }, cancellationToken);
            };
        });
        return services;
    }

    /// <summary>Resolves delivery at startup, including factories, so a production preview sender cannot slip through.</summary>
    public static void ValidateAccountEmailDelivery(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        if (!app.Environment.IsDevelopment() && sender is DevelopmentPreviewEmailSender)
        {
            throw new InvalidOperationException("A real email sender is required outside Development.");
        }
    }

    /// <summary>Validates the trusted link origin instead of trusting an incoming Host header.</summary>
    private static bool IsTrustedApplicationUrl(string value, bool isDevelopment)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (isDevelopment && uri.Scheme == Uri.UriSchemeHttp))
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }
}
