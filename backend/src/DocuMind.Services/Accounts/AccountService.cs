using System.Text;
using DocuMind.Data.Entities;
using DocuMind.Services.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DocuMind.Services.Accounts;

/// <summary>Coordinates Identity account creation, confirmation tokens, and email delivery.</summary>
public sealed class AccountService(UserManager<ApplicationUser> users, IEmailSender emailSender,
    IOptions<AccountOptions> options, IMemoryCache deliveryCooldowns, ILogger<AccountService> logger)
    : IAccountService
{
    // Scoped account services share one memory cache. Only the short check/set is locked;
    // database work and email delivery never run while this lock is held.
    private static readonly object DeliveryCooldownLock = new();

    /// <inheritdoc />
    public async Task<RegistrationResult> RegisterAsync(string email, string password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = new ApplicationUser { UserName = email.Trim(), Email = email.Trim(), EmailConfirmed = false };

        // Validate passwords before looking for an existing email so weak-password responses
        // cannot reveal account existence. Identity remains the source of the password policy.
        var passwordErrors = new List<string>();
        foreach (var validator in users.PasswordValidators)
        {
            var validation = await validator.ValidateAsync(users, user, password);
            passwordErrors.AddRange(validation.Errors.Select(error => error.Description));
        }

        if (passwordErrors.Count > 0)
        {
            return new RegistrationResult(passwordErrors.Distinct().ToArray());
        }

        // Identity user rules (such as allowed username characters) must also run before
        // an existence shortcut. Discard uniqueness errors, but keep other validation
        // identical even for legacy accounts created outside this registration endpoint.
        var userErrors = new List<string>();
        foreach (var validator in users.UserValidators)
        {
            var validation = await validator.ValidateAsync(users, user);
            userErrors.AddRange(validation.Errors
                .Where(error => error.Code is not "DuplicateEmail" and not "DuplicateUserName")
                .Select(error => error.Description));
        }

        if (userErrors.Count > 0)
        {
            return new RegistrationResult(userErrors.Distinct().ToArray());
        }

        if (await users.FindByEmailAsync(user.Email) is not null)
        {
            // Do comparable password-hashing work, but never replace an existing account's password.
            // Equal response content is guaranteed; this does not claim perfect timing equivalence.
            _ = users.PasswordHasher.HashPassword(user, password);
            return new RegistrationResult([]);
        }

        IdentityResult created;
        try
        {
            // Identity validates the user and password again, hashes the password, and saves it.
            // Using the email as username also makes the existing unique username index protect races.
            created = await users.CreateAsync(user, password);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UserNameIndex" })
        {
            return new RegistrationResult([]);
        }

        if (!created.Succeeded)
        {
            // Duplicate-email/username errors are never exposed to anonymous callers.
            var errors = created.Errors.Where(error => error.Code is not "DuplicateEmail" and not "DuplicateUserName")
                .Select(error => error.Description).ToArray();
            return new RegistrationResult(errors);
        }

        await SendConfirmationAsync(user, cancellationToken);
        return new RegistrationResult([]);
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmEmailAsync(string userId, string encodedToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParse(userId, out _) || encodedToken.Length > 4096)
        {
            return false;
        }

        string token;
        try
        {
            // Decode the same UTF-8 + Base64Url transformation used when creating email links.
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(encodedToken));
        }
        catch (FormatException)
        {
            return false;
        }

        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            return false;
        }

        // Identity checks the token purpose, security stamp, user binding, and expiration.
        // Even already-confirmed accounts must supply a valid token; no bypass is added here.
        return (await users.ConfirmEmailAsync(user, token)).Succeeded;
    }

    /// <inheritdoc />
    public async Task ResendConfirmationAsync(string email, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is not null && !user.EmailConfirmed)
        {
            await SendConfirmationAsync(user, cancellationToken);
        }
    }

    /// <summary>Builds a trusted link and limits repeated delivery without returning account details.</summary>
    private async Task SendConfirmationAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var cooldownKey = $"email-confirmation:{user.Id}";
        lock (DeliveryCooldownLock)
        {
            if (deliveryCooldowns.TryGetValue(cooldownKey, out _))
            {
                return;
            }

            deliveryCooldowns.Set(cooldownKey, true, options.Value.ResendCooldown);
        }

        try
        {
            var token = await users.GenerateEmailConfirmationTokenAsync(user);
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var endpoint = options.Value.ApplicationUrl.TrimEnd('/') + "/api/v1/auth/confirm-email";
            var link = QueryHelpers.AddQueryString(endpoint, new Dictionary<string, string?>
            {
                ["userId"] = user.Id.ToString(), ["token"] = encodedToken
            });
            var body = $"Confirm your DocuMind email address using this link:\n{link}\n\n" +
                "If you did not request this account, ignore this message.";
            await emailSender.SendAsync(user.Email!, "Confirm your DocuMind email", body, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Delivery failure must not turn a new-account response into an existence oracle.
            // Resend can retry later. Do not log the address, token, body, or provider exception message.
            deliveryCooldowns.Remove(cooldownKey);
            logger.LogError("Confirmation email delivery failed ({ErrorType}); the account can request a resend.",
                exception.GetType().Name);
        }
    }
}
