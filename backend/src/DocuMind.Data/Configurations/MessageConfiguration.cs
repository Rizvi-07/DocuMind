using DocuMind.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocuMind.Data.Configurations;

/// <summary>Enforces deterministic message ordering and stores only recognized chat roles.</summary>
public sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    /// <summary>Messages inherit ownership through the conversation and are removed with that conversation.</summary>
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages", table =>
        {
            table.HasCheckConstraint("CK_Messages_Position", "\"Position\" >= 0");
            table.HasCheckConstraint("CK_Messages_Content", "btrim(\"Content\") <> '' AND char_length(\"Content\") <= 64000");
            table.HasCheckConstraint("CK_Messages_Role", ModelConstraints.EnumValues<MessageRole>("Role"));
        });
        builder.HasKey(message => message.Id);
        builder.Property(message => message.Content).HasColumnType("text").IsRequired();
        builder.Property(message => message.Role).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(message => message.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.HasOne(message => message.Conversation).WithMany(conversation => conversation.Messages)
            .HasForeignKey(message => message.ConversationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(message => new { message.ConversationId, message.Position }).IsUnique();
    }
}
