using DocuMind.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocuMind.Data.Configurations;

/// <summary>Stores user chat history and rejects cross-user document scopes at the foreign-key boundary.</summary>
public sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    /// <summary>Document deletion is restricted while referenced; callers must explicitly preserve or delete scoped history.</summary>
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("Conversations", table => table.HasCheckConstraint("CK_Conversations_Title", "btrim(\"Title\") <> ''"));
        builder.HasKey(conversation => conversation.Id);
        builder.Property(conversation => conversation.Title).HasMaxLength(200).IsRequired();
        builder.Property(conversation => conversation.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.HasOne(conversation => conversation.Owner).WithMany().HasForeignKey(conversation => conversation.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
        // OwnerId participates in both FKs: even direct SQL cannot attach another user's document.
        builder.HasOne(conversation => conversation.Document).WithMany()
            .HasForeignKey(conversation => new { conversation.DocumentId, conversation.OwnerId })
            .HasPrincipalKey(document => new { document.Id, document.OwnerId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(conversation => new { conversation.OwnerId, conversation.CreatedAt, conversation.Id }).IsDescending(false, true, false);
    }
}
