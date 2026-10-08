using System.ComponentModel.DataAnnotations;
using DocuMind.Api.Contracts;
using Xunit;

namespace DocuMind.UnitTests;

/// <summary>Tests application input bounds separately from Identity strength rules and MVC's HTTP validation pipeline.</summary>
public sealed class AccountRequestValidationTests
{
    /// <summary>Registration rejects missing, malformed, oversized, and mismatched inputs with field-specific errors.</summary>
    [Theory]
    [InlineData("empty-email", "Email")]
    [InlineData("invalid-email", "Email")]
    [InlineData("long-email", "Email")]
    [InlineData("empty-password", "Password")]
    [InlineData("short-password", "Password")]
    [InlineData("long-password", "Password")]
    [InlineData("empty-confirmation", "ConfirmPassword")]
    [InlineData("long-confirmation", "ConfirmPassword")]
    [InlineData("mismatch", "ConfirmPassword")]
    public void Registration_InvalidInput_ReportsAffectedField(string invalid, string field)
    {
        var password = Guid.NewGuid().ToString("N");
        var email = Email(invalid);
        var attempted = invalid switch
        {
            "empty-password" => "", "short-password" => password[..11],
            "long-password" => new string('x', 129), _ => password
        };
        var confirmation = invalid switch
        {
            "empty-confirmation" => "", "long-confirmation" => new string('x', 129),
            "mismatch" => password + "different", _ => attempted
        };

        var errors = Validate(new RegisterRequest { Email = email, Password = attempted, ConfirmPassword = confirmation });

        Assert.Contains(errors, error => error.MemberNames.Contains(field));
    }

    /// <summary>Registration's minimum/maximum sizes pass annotations; Identity separately checks password strength.</summary>
    [Theory]
    [InlineData(12)]
    [InlineData(128)]
    public void Registration_BoundaryPasswordLengths_AreAcceptedByAnnotations(int length)
    {
        var password = new string('x', length);
        Assert.Empty(Validate(new RegisterRequest { Email = "account@example.test", Password = password, ConfirmPassword = password }));
    }

    /// <summary>Login rejects malformed and oversized inputs without revealing any stored account information.</summary>
    [Theory]
    [InlineData("empty-email", "Email")]
    [InlineData("invalid-email", "Email")]
    [InlineData("long-email", "Email")]
    [InlineData("empty-password", "Password")]
    [InlineData("long-password", "Password")]
    public void Login_InvalidInput_ReportsAffectedField(string invalid, string field)
    {
        var password = invalid switch
        {
            "empty-password" => "", "long-password" => new string('x', 129),
            _ => Guid.NewGuid().ToString("N")
        };
        Assert.Contains(Validate(new LoginRequest { Email = Email(invalid), Password = password }),
            error => error.MemberNames.Contains(field));
    }

    /// <summary>Short wrong passwords reach Identity's failure counting instead of being rejected by a registration-strength rule.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    public void Login_BoundedPassword_IsAcceptedWithoutRegistrationMinimum(int length)
    {
        var request = new LoginRequest { Email = "account@example.test", Password = new string('x', length) };
        Assert.Empty(Validate(request));
        Assert.False(request.RememberMe);
    }

    /// <summary>Resend requires a bounded valid email; delivery eligibility belongs to the service.</summary>
    [Theory]
    [InlineData("empty-email")]
    [InlineData("invalid-email")]
    [InlineData("long-email")]
    public void Resend_InvalidEmail_IsRejected(string invalid)
        => Assert.Contains(Validate(new ResendConfirmationRequest { Email = Email(invalid) }),
            error => error.MemberNames.Contains(nameof(ResendConfirmationRequest.Email)));

    /// <summary>A valid resend email passes input validation without checking whether it is registered.</summary>
    [Fact]
    public void Resend_ValidEmail_IsAccepted()
        => Assert.Empty(Validate(new ResendConfirmationRequest { Email = "account@example.test" }));

    /// <summary>Confirmation values are required and bounded before service token decoding.</summary>
    [Theory]
    [InlineData("empty-id", "UserId")]
    [InlineData("long-id", "UserId")]
    [InlineData("empty-token", "Token")]
    [InlineData("long-token", "Token")]
    public void Confirmation_InvalidBounds_ReportAffectedField(string invalid, string field)
    {
        var request = new ConfirmEmailRequest
        {
            UserId = invalid switch { "empty-id" => "", "long-id" => new string('1', 37), _ => Guid.NewGuid().ToString() },
            Token = invalid switch { "empty-token" => "", "long-token" => new string('a', 4097), _ => "synthetic-token" }
        };
        Assert.Contains(Validate(request), error => error.MemberNames.Contains(field));
    }

    /// <summary>The largest supported token is accepted by annotations; its signature is still checked by Identity.</summary>
    [Fact]
    public void Confirmation_BoundaryTokenLength_IsAccepted()
        => Assert.Empty(Validate(new ConfirmEmailRequest { UserId = Guid.NewGuid().ToString(), Token = new string('a', 4096) }));

    /// <summary>Provides only synthetic addresses, including a 255-character value beyond the API limit.</summary>
    private static string Email(string invalid) => invalid switch
    {
        "empty-email" => "", "invalid-email" => "not-an-email", "long-email" => new string('a', 242) + "@example.test",
        _ => "account@example.test"
    };

    /// <summary>Runs the same declared DataAnnotations without starting MVC or bypassing validation via manual assumptions.</summary>
    private static List<ValidationResult> Validate(object request)
    {
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), errors, validateAllProperties: true);
        return errors;
    }
}
