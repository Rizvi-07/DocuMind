using DocuMind.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocuMind.Data.Configurations;

/// <summary>Enforces ownership, opaque storage identity, and safe processing metadata independently of HTTP validation.</summary>
public sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    /// <summary>User deletion owns document lifetime; deleting a document also removes its chunks and processing history.</summary>
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("Documents", table =>
        {
            table.HasCheckConstraint("CK_Documents_FileName", "btrim(\"DisplayFileName\") <> ''");
            table.HasCheckConstraint("CK_Documents_StorageKey", "\"StorageKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_-]{15,127}$'");
            table.HasCheckConstraint("CK_Documents_Status", ModelConstraints.EnumValues<DocumentProcessingStatus>("Status"));
            table.HasCheckConstraint("CK_Documents_FailureCode", ModelConstraints.EnumValues<ProcessingFailureCode>("FailureCode", true));
            table.HasCheckConstraint("CK_Documents_FailureState", "(\"Status\" = 'Failed') = (\"FailureCode\" IS NOT NULL)");
        });
        builder.HasKey(document => document.Id);
        // The alternate key lets a conversation's optional document FK include the authenticated owner's ID.
        builder.HasAlternateKey(document => new { document.Id, document.OwnerId });
        builder.Property(document => document.DisplayFileName).HasMaxLength(255).IsRequired();
        builder.Property(document => document.StorageKey).HasMaxLength(128).IsRequired();
        builder.Property(document => document.UploadedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Property(document => document.Status).HasConversion<string>().HasMaxLength(24).HasDefaultValue(DocumentProcessingStatus.Pending);
        builder.Property(document => document.FailureCode).HasConversion<string>().HasMaxLength(32);
        builder.Ignore(document => document.FailureMessage);
        builder.HasOne(document => document.Owner).WithMany().HasForeignKey(document => document.OwnerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(document => document.StorageKey).IsUnique();
        builder.HasIndex(document => new { document.OwnerId, document.UploadedAt, document.Id }).IsDescending(false, true, false);
    }
}
