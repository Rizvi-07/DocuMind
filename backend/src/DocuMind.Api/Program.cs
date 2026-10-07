using DocuMind.Api.Configuration;
using DocuMind.Services.Accounts;
using DocuMind.Data;
using DocuMind.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddAuthorization();
builder.Services.AddOpenApi();

// Account services validate link settings and require real delivery outside Development.
builder.Services.AddDocuMindAccounts(builder.Configuration, builder.Environment);

var connectionString =
    builder.Configuration.GetConnectionString("DocuMind")
    ?? throw new InvalidOperationException(
        "Connection string 'DocuMind' is missing.");

builder.Services.AddDbContext<DocuMindDbContext>(options =>
    options.UseNpgsql(connectionString));

// Register Identity's user, role, and sign-in services.
// AddIdentity also registers Identity's authentication cookies.
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
    {
        // Prevent multiple accounts from using the same email address.
        options.User.RequireUniqueEmail = true;

        // Require passwords with at least 12 characters
        // and a mixture of numbers, letters, and symbols.
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;

        // Allow new accounts to be locked temporarily.
        options.Lockout.AllowedForNewUsers = true;

        // Configure a five-minute lockout after five failed attempts.
        // Our login code must enable failure counting to use this policy.
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan =
            TimeSpan.FromMinutes(5);

        // Require email confirmation before allowing login.
        // Registration sends a token link; the confirmation endpoint verifies ownership.
        options.SignIn.RequireConfirmedEmail = true;

        // Keep email-token expiration independent from future password-reset tokens.
        options.Tokens.EmailConfirmationTokenProvider = EmailConfirmationTokenProvider.ProviderName;
    })

    // Store Identity's account information through our EF Core context.
    .AddEntityFrameworkStores<DocuMindDbContext>()

    // Register token providers for operations such as
    // email confirmation and password reset.
    .AddDefaultTokenProviders()
    .AddTokenProvider<EmailConfirmationTokenProvider>(EmailConfirmationTokenProvider.ProviderName);

// Configure the cookie Identity uses for signed-in sessions.
builder.Services.ConfigureApplicationCookie(options =>
{
    // Give the authentication cookie a recognizable application name.
    options.Cookie.Name = "DocuMind.Auth";

    // Prevent browser JavaScript from reading the cookie.
    options.Cookie.HttpOnly = true;

    // Restrict cookie sending in common cross-site request scenarios.
    // State-changing endpoints will also need CSRF protection.
    options.Cookie.SameSite = SameSiteMode.Lax;

    // Allow local HTTP development.
    // Outside Development, send the cookie only over HTTPS.
    options.Cookie.SecurePolicy =
        builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;

    // Set the authentication ticket's lifetime to 60 minutes.
    options.ExpireTimeSpan = TimeSpan.FromMinutes(60);

    // Renew the ticket on eligible requests as the user remains active.
    options.SlidingExpiration = true;

    // Return 401 for an unauthenticated request to a protected endpoint.
    // API clients should receive a status code instead of a login redirect.
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode =
            StatusCodes.Status401Unauthorized;

        return Task.CompletedTask;
    };

    // Return 403 when a signed-in user lacks the required permission.
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode =
            StatusCodes.Status403Forbidden;

        return Task.CompletedTask;
    };
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<DocuMindDbContext>("postgresql");

var app = builder.Build();

// Resolve email delivery before accepting requests, so invalid sender registrations fail at startup.
app.ValidateAccountEmailDelivery();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    app.UseHttpsRedirection();
}

// Identify the user before checking authorization.
// Explicit routing ensures endpoint-specific rate-limit metadata is available.
app.UseRouting();
app.UseRateLimiter();

app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/api/v1/health/ready");

app.Run();