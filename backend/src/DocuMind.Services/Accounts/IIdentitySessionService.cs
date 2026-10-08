using System.Security.Claims;

namespace DocuMind.Services.Accounts;

/// <summary>Owns Identity sign-in/out decisions while controllers own their HTTP responses.</summary>
public interface IIdentitySessionService
{
    /// <summary>Creates an Identity cookie only after password, confirmation, and lockout checks succeed.</summary>
    Task<bool> LoginAsync(string email, string password, bool rememberMe, CancellationToken cancellationToken);

    /// <summary>Clears Identity's authentication cookies for the current browser session.</summary>
    Task LogoutAsync(CancellationToken cancellationToken);

    /// <summary>Maps the authenticated principal to safe fields instead of returning an Identity entity.</summary>
    Task<CurrentAccount?> GetCurrentAccountAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
}

/// <summary>Contains only the account fields needed by the signed-in user's UI.</summary>
public sealed record CurrentAccount(Guid Id, string? Email, bool EmailConfirmed, DateTimeOffset CreatedAt);
