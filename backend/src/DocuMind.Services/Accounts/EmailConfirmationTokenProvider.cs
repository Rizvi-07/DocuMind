using DocuMind.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocuMind.Services.Accounts;

/// <summary>Gives email confirmation its own token protection purpose and expiration settings.</summary>
public sealed class EmailConfirmationTokenProvider : DataProtectorTokenProvider<ApplicationUser>
{
    /// <summary>The name assigned to Identity's email confirmation provider.</summary>
    public const string ProviderName = "DocuMindEmailConfirmation";

    /// <summary>Uses Identity's token implementation rather than inventing a token format or signature.</summary>
    public EmailConfirmationTokenProvider(IDataProtectionProvider protection,
        IOptions<EmailConfirmationTokenOptions> options,
        ILogger<DataProtectorTokenProvider<ApplicationUser>> logger)
        : base(protection, options, logger)
    {
    }
}

/// <summary>Keeps confirmation expiration separate from future password-reset tokens.</summary>
public sealed class EmailConfirmationTokenOptions : DataProtectionTokenProviderOptions
{
    /// <summary>Uses a distinct Data Protection purpose so tokens cannot cross application workflows.</summary>
    public EmailConfirmationTokenOptions()
    {
        Name = EmailConfirmationTokenProvider.ProviderName;
    }
}
