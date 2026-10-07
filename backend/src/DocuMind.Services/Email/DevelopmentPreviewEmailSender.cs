using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace DocuMind.Services.Email;

/// <summary>Writes local email previews instead of contacting an email provider in Development.</summary>
public sealed class DevelopmentPreviewEmailSender : IEmailSender
{
    private readonly string previewDirectory;

    /// <summary>Rejects non-Development use and locates the repository's ignored storage directory.</summary>
    public DevelopmentPreviewEmailSender(IHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Development email previews cannot be used outside Development.");
        }

        // The API lives in backend/src/DocuMind.Api; backend/storage is ignored by Git.
        // Preview files contain bearer tokens and must never be served as static web content.
        previewDirectory = Path.GetFullPath(Path.Combine(
            environment.ContentRootPath, "..", "..", "storage", "email-previews"));
    }

    /// <inheritdoc />
    public async Task SendAsync(string recipient, string subject, string textBody, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(previewDirectory);
        var filename = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json";
        var preview = new { To = recipient, Subject = subject, TextBody = textBody };

        // Random names avoid collisions and never embed user email addresses in filenames.
        await using var stream = new FileStream(Path.Combine(previewDirectory, filename),
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, preview,
            new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
    }
}
