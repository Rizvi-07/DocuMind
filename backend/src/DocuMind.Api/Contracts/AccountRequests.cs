using System.ComponentModel.DataAnnotations;

namespace DocuMind.Api.Contracts;

/// <summary>Validates HTTP registration input before the account service is called.</summary>
public sealed class RegisterRequest
{
    /// <summary>The account email; Identity separately enforces uniqueness.</summary>
    [Required, EmailAddress, StringLength(254)]
    public string Email { get; init; } = string.Empty;

    /// <summary>Bounds input size; Identity validators enforce the configured strength requirements.</summary>
    [Required, StringLength(128, MinimumLength = 12)]
    public string Password { get; init; } = string.Empty;

    /// <summary>Prevents an accidental password typo without persisting a second password value.</summary>
    [Required, Compare(nameof(Password)), StringLength(128)]
    public string ConfirmPassword { get; init; } = string.Empty;
}

/// <summary>Validates the email address supplied to the anonymous resend endpoint.</summary>
public sealed class ResendConfirmationRequest
{
    /// <summary>The destination to consider; the response never reports whether it is registered.</summary>
    [Required, EmailAddress, StringLength(254)]
    public string Email { get; init; } = string.Empty;
}

/// <summary>Bounds the confirmation query/body before handing token validation to Identity.</summary>
public sealed class ConfirmEmailRequest
{
    /// <summary>The user ID carried by the protected email link, not a login credential.</summary>
    [Required, StringLength(36)]
    public string UserId { get; init; } = string.Empty;

    /// <summary>A URL-safe token; never include this value in application logs.</summary>
    [Required, StringLength(4096)]
    public string Token { get; init; } = string.Empty;
}

/// <summary>Bounds login input without rejecting short wrong passwords before Identity can count them.</summary>
public sealed class LoginRequest
{
    /// <summary>The sign-in email; authentication failures do not disclose whether it exists.</summary>
    [Required, EmailAddress, StringLength(254)]
    public string Email { get; init; } = string.Empty;

    /// <summary>The attempted password; Identity verifies the stored hash.</summary>
    [Required, StringLength(128)]
    public string Password { get; init; } = string.Empty;

    /// <summary>Opts into a persistent browser cookie; false creates a browser-session cookie.</summary>
    public bool RememberMe { get; init; }
}
