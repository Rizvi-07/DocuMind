namespace DocuMind.Data.Entities;

/// <summary>Stores one chat turn; ownership is inherited through its conversation, never through a separate user input.</summary>
public sealed class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    /// <summary>Zero-based sequence allocated transactionally by Services; uniqueness prevents ambiguous chat ordering.</summary>
    public int Position { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
