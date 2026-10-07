namespace DocuMind.Services.Accounts;

/// <summary>Controls trusted confirmation links and their delivery lifetime.</summary>
public sealed class AccountOptions
{
    /// <summary>The configuration section shared by the API and account services.</summary>
    public const string SectionName = "Accounts";

    /// <summary>The public API base URL; never derive email links from a request Host header.</summary>
    public string ApplicationUrl { get; set; } = string.Empty;

    /// <summary>How long Identity accepts a confirmation token after it is generated.</summary>
    public TimeSpan ConfirmationTokenLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Limits repeated email delivery for one account, including across different client IPs.</summary>
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromMinutes(1);
}
