using DocuMind.Api.Configuration;
using DocuMind.Services.Accounts;
using DocuMind.Data;
using DocuMind.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// MVC view services register the built-in antiforgery authorization filter used by our JSON APIs.
// Endpoints remain controller-based; no Razor pages or view routes are mapped.
builder.Services.AddControllersWithViews();
builder.Services.AddAuthorization();
builder.Services.AddOpenApi();

// Account services validate link settings and require real delivery outside Development.
builder.Services.AddDocuMindAccounts(builder.Configuration, builder.Environment);

var connectionString =
    builder.Configuration.GetConnectionString("DocuMind")
    ?? throw new InvalidOperationException(
        "Connection string 'DocuMind' is missing.");

builder.Services.AddDbContext<DocuMindDbContext>(options =>
    options.UseDocuMindPostgreSql(connectionString));

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
        // Login opts into failure counting through SignInManager.
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

// Share secure cookie and CSRF settings with every controller endpoint.
builder.Services.AddDocuMindBrowserAuthentication(builder.Environment);

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