namespace DocuMind.Services.Accounts;

/// <summary>Defines account use cases without exposing HTTP or Identity implementation details to callers.</summary>
public interface IAccountService
{
    /// <summary>Validates and creates an unconfirmed account; duplicates use the same accepted result.</summary>
    Task<RegistrationResult> RegisterAsync(string email, string password, CancellationToken cancellationToken);

    /// <summary>Confirms ownership only when Identity validates the supplied URL-safe token.</summary>
    Task<bool> ConfirmEmailAsync(string userId, string encodedToken, CancellationToken cancellationToken);

    /// <summary>Attempts delivery for an eligible account while keeping account existence private.</summary>
    Task ResendConfirmationAsync(string email, CancellationToken cancellationToken);
}

/// <summary>Contains only safe validation feedback; an empty error list means the request was accepted.</summary>
public sealed record RegistrationResult(string[] Errors);
