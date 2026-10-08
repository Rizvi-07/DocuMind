namespace DocuMind.Data.Entities;

/// <summary>A user-owned chat, optionally scoped to one document owned by the same user.</summary>
public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public ApplicationUser Owner { get; set; } = null!;
    /// <summary>Null means the user's available corpus; a composite foreign key prevents cross-owner document scopes.</summary>
    public Guid? DocumentId { get; set; }
    public Document? Document { get; set; }
    public string Title { get; set; } = "New conversation";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<Message> Messages { get; set; } = new List<Message>();
}
