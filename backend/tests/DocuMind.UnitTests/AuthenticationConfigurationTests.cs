using DocuMind.Api.Configuration;
using DocuMind.Services.Accounts;
using DocuMind.Services.Email;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace DocuMind.UnitTests;

/// <summary>Protects application-owned cookie, antiforgery, email-sender guards, and trusted-link configuration.</summary>
public sealed class AuthenticationConfigurationTests
{
    /// <summary>Both cookie families require HTTPS outside Development while keeping their browser security settings.</summary>
    [Theory]
    [InlineData("Development", CookieSecurePolicy.SameAsRequest)]
    [InlineData("Production", CookieSecurePolicy.Always)]
    [InlineData("Staging", CookieSecurePolicy.Always)]
    public void BrowserOptions_PreserveSecureCookiesAndGlobalAntiforgery(string environment, CookieSecurePolicy securePolicy)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDocuMindBrowserAuthentication(Host(environment));
        using var provider = services.BuildServiceProvider();
        var cookie = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var csrf = provider.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;

        Assert.Equal("DocuMind.Auth", cookie.Cookie.Name);
        Assert.True(cookie.Cookie.HttpOnly);
        Assert.Equal("/", cookie.Cookie.Path);
        Assert.Equal(SameSiteMode.Lax, cookie.Cookie.SameSite);
        Assert.Equal(securePolicy, cookie.Cookie.SecurePolicy);
        Assert.Equal(TimeSpan.FromMinutes(60), cookie.ExpireTimeSpan);
        Assert.True(cookie.SlidingExpiration);
        Assert.Equal("DocuMind.Csrf", csrf.Cookie.Name);
        Assert.True(csrf.Cookie.HttpOnly);
        Assert.Equal("/", csrf.Cookie.Path);
        Assert.Equal(SameSiteMode.Strict, csrf.Cookie.SameSite);
        Assert.Equal(securePolicy, csrf.Cookie.SecurePolicy);
        Assert.Equal("X-CSRF-TOKEN", csrf.HeaderName);
        Assert.Contains(provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters,
            filter => filter is AutoValidateAntiforgeryTokenAttribute);
    }

    /// <summary>API clients receive status codes instead of login or permission-denied HTML redirects.</summary>
    [Theory]
    [InlineData(true, 401)]
    [InlineData(false, 403)]
    public async Task CookieRedirectHandlers_ReturnStatusWithoutRedirect(bool login, int expectedStatus)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDocuMindBrowserAuthentication(Host(Environments.Development));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var context = new DefaultHttpContext();
        var scheme = new AuthenticationScheme(IdentityConstants.ApplicationScheme, null, typeof(CookieAuthenticationHandler));
        var redirect = new RedirectContext<CookieAuthenticationOptions>(context, scheme, options,
            new AuthenticationProperties(), "https://ignored.example.test/login");

        if (login) await options.Events.OnRedirectToLogin(redirect);
        else await options.Events.OnRedirectToAccessDenied(redirect);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
    }

    /// <summary>Trusted links allow local HTTP only in Development and reject URL components that could carry secrets.</summary>
    [Theory]
    [InlineData("Development", "http://localhost:3000", true)]
    [InlineData("Production", "https://app.example.test/documind", true)]
    [InlineData("Staging", "http://app.example.test", false)]
    [InlineData("Production", "http://app.example.test", false)]
    [InlineData("Development", "relative/path", false)]
    [InlineData("Development", "file:///local/path", false)]
    [InlineData("Development", "https://user:synthetic@app.example.test", false)]
    [InlineData("Development", "https://app.example.test?private=value", false)]
    [InlineData("Development", "https://app.example.test#fragment", false)]
    public void AccountOptions_ValidateTrustedApplicationUrl(string environment, string url, bool valid)
    {
        using var provider = AccountProvider(environment, url);
        var options = provider.GetRequiredService<IOptions<AccountOptions>>();
        if (valid) Assert.Equal(url, options.Value.ApplicationUrl);
        else Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    /// <summary>Token and delivery time settings have explicit lower/upper limits, including the boundary values.</summary>
    [Theory]
    [InlineData("00:00:01", "00:00:01", true)]
    [InlineData("7.00:00:00", "1.00:00:00", true)]
    [InlineData("00:00:00", "00:01:00", false)]
    [InlineData("7.00:00:01", "00:01:00", false)]
    [InlineData("01:00:00", "00:00:00", false)]
    [InlineData("01:00:00", "1.00:00:01", false)]
    public void AccountOptions_ValidateLifetimeAndCooldown(string lifetime, string cooldown, bool valid)
    {
        using var provider = AccountProvider(Environments.Development, "http://localhost:3000", lifetime, cooldown);
        var options = provider.GetRequiredService<IOptions<AccountOptions>>();
        if (valid) Assert.Equal(TimeSpan.Parse(lifetime), options.Value.ConfirmationTokenLifetime);
        else Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    /// <summary>The configured confirmation expiration and protection name do not change future reset-token defaults.</summary>
    [Fact]
    public void ConfirmationOptions_UseDedicatedLifetimeAndPurpose()
    {
        using var provider = AccountProvider(Environments.Development, "http://localhost:3000", "04:00:00");
        var confirmation = provider.GetRequiredService<IOptions<EmailConfirmationTokenOptions>>().Value;
        var defaultTokens = provider.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value;

        Assert.Equal(TimeSpan.FromHours(4), confirmation.TokenLifespan);
        Assert.Equal(EmailConfirmationTokenProvider.ProviderName, confirmation.Name);
        Assert.Equal(TimeSpan.FromDays(1), defaultTokens.TokenLifespan);
        Assert.NotEqual(confirmation.Name, defaultTokens.Name);
    }

    /// <summary>Non-Development startup must not silently select preview email or a no-op implementation.</summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Accounts_NoProductionSender_FailsClearly(string environment)
    {
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => services.AddDocuMindAccounts(
            Configuration("https://app.example.test"), Host(environment)));
        Assert.Contains("No production email sender is configured", error.Message);
    }

    /// <summary>Development defaults never overwrite an explicitly supplied email implementation.</summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Accounts_PreservesExplicitSenderRegistration(string environment)
    {
        var services = new ServiceCollection();
        var sender = Mock.Of<IEmailSender>();
        services.AddSingleton(sender);
        services.AddDocuMindAccounts(Configuration("https://app.example.test"), Host(environment));
        using var provider = services.BuildServiceProvider();
        Assert.Same(sender, provider.GetRequiredService<IEmailSender>());
    }

    /// <summary>The Development sender itself rejects use outside Development even if registered accidentally.</summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void PreviewSender_RejectsNonDevelopment(string environment)
    {
        var error = Assert.Throws<InvalidOperationException>(() => new DevelopmentPreviewEmailSender(Host(environment)));
        Assert.Contains("cannot be used outside Development", error.Message);
    }

    /// <summary>Builds test-only DI options; production sender doubles exist only in this unit-test project.</summary>
    private static ServiceProvider AccountProvider(string environment, string url,
        string lifetime = "1.00:00:00", string cooldown = "00:01:00")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IEmailSender>());
        services.AddDocuMindAccounts(Configuration(url, lifetime, cooldown), Host(environment));
        return services.BuildServiceProvider();
    }

    /// <summary>Uses in-memory public settings so unit tests never load local user secrets or environment files.</summary>
    private static IConfiguration Configuration(string url, string lifetime = "1.00:00:00", string cooldown = "00:01:00")
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Accounts:ApplicationUrl"] = url, ["Accounts:ConfirmationTokenLifetime"] = lifetime,
            ["Accounts:ResendCooldown"] = cooldown
        }).Build();

    /// <summary>Supplies only environment metadata; no host is started and no preview path is created.</summary>
    private static IHostEnvironment Host(string name)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(host => host.EnvironmentName).Returns(name);
        return environment.Object;
    }
}
