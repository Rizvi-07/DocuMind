using System.Security.Claims;
using DocuMind.Data.Entities;
using Microsoft.AspNetCore.Identity;

namespace DocuMind.Services.Accounts;

/// <summary>Uses SignInManager for cookie sessions and preserves the configured Identity policies.</summary>
public sealed class IdentitySessionService(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn)
    : IIdentitySessionService
{
    /// <inheritdoc />
    public async Task<bool> LoginAsync(string email, string password, bool rememberMe,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            // Do password-hashing work for unknown accounts too; never return a different failure reason.
            // Response contents are uniform, but perfect timing equivalence is not promised.
            _ = users.PasswordHasher.HashPassword(new ApplicationUser(), password);
            return false;
        }

        // SignInManager enforces RequireConfirmedEmail and the existing five-attempt lockout policy.
        // lockoutOnFailure is essential: false would let incorrect passwords bypass failure counting.
        var result = await signIn.PasswordSignInAsync(user, password, rememberMe, lockoutOnFailure: true);
        return result.Succeeded;
    }

    /// <inheritdoc />
    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await signIn.SignOutAsync();
    }

    /// <inheritdoc />
    public async Task<CurrentAccount?> GetCurrentAccountAsync(ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = await users.GetUserAsync(principal);
        return user is null ? null : new CurrentAccount(user.Id, user.Email, user.EmailConfirmed, user.CreatedAt);
    }
}
