using Microsoft.AspNetCore.Mvc;

namespace DocuMind.Api.Configuration;

/// <summary>Configures browser cookie security and token-based CSRF protection in one place.</summary>
public static class BrowserAuthenticationConfiguration
{
    /// <summary>The header used by same-origin browser mutations to return their request token.</summary>
    public const string CsrfHeaderName = "X-CSRF-TOKEN";

    /// <summary>Preserves Identity cookie settings and requires CSRF validation on all unsafe controller requests.</summary>
    public static IServiceCollection AddDocuMindBrowserAuthentication(this IServiceCollection services,
        IHostEnvironment environment)
    {
        var securePolicy = environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "DocuMind.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.Path = "/";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = securePolicy;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(60);
            options.SlidingExpiration = true;

            // API clients receive status codes instead of HTML login/access-denied redirects.
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        services.AddAntiforgery(options =>
        {
            options.HeaderName = CsrfHeaderName;
            options.Cookie.Name = "DocuMind.Csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.Path = "/";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = securePolicy;
        });

        // Apply token validation globally, including anonymous registration and login.
        // GET/HEAD/OPTIONS stay safe; POST/PUT/PATCH/DELETE need the cookie plus request token.
        services.Configure<MvcOptions>(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
        return services;
    }
}
