namespace DocuMind.Data.Entities;

/// <summary>Stores user-owned upload metadata; file bytes remain in private storage outside database rows.</summary>
public sealed class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Always assigned from the authenticated principal, never from browser input.</summary>
    public Guid OwnerId { get; set; }
    public ApplicationUser Owner { get; set; } = null!;
    /// <summary>A display label only; never combine this untrusted filename with a filesystem path.</summary>
    public string DisplayFileName { get; set; } = string.Empty;
    /// <summary>An opaque application-generated key; not an absolute path, URL, or original filename.</summary>
    public string StorageKey { get; set; } = string.Empty;
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
    public DocumentProcessingStatus Status { get; set; } = DocumentProcessingStatus.Pending;
    /// <summary>Only allowlisted codes are persisted; the public message is derived rather than storing exceptions.</summary>
    public ProcessingFailureCode? FailureCode { get; set; }
    public string? FailureMessage => ProcessingFailures.GetSafeMessage(FailureCode);
    public ICollection<DocumentChunk> Chunks { get; set; } = new List<DocumentChunk>();
    public ICollection<ProcessingJob> ProcessingJobs { get; set; } = new List<ProcessingJob>();
}
