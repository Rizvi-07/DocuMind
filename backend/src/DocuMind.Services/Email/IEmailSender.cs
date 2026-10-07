namespace DocuMind.Services.Email;

/// <summary>Separates account workflows from the selected email delivery mechanism.</summary>
public interface IEmailSender
{
    /// <summary>Sends a plain-text message without logging its recipient or confirmation token.</summary>
    Task SendAsync(string recipient, string subject, string textBody, CancellationToken cancellationToken);
}
