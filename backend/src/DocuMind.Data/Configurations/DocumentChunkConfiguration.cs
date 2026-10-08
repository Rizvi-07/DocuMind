using DocuMind.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocuMind.Data.Configurations;

/// <summary>Restricts all persisted vectors to the selected model/dimension contract and orders chunks within their document.</summary>
public sealed class DocumentChunkConfiguration : IEntityTypeConfiguration<DocumentChunk>
{
    /// <summary>Creates cosine indexing only over completed vectors; pending chunks may retain text without an embedding.</summary>
    public void Configure(EntityTypeBuilder<DocumentChunk> builder)
    {
        builder.ToTable("DocumentChunks", table =>
        {
            table.HasCheckConstraint("CK_DocumentChunks_Position", "\"Position\" >= 0");
            table.HasCheckConstraint("CK_DocumentChunks_Text", "btrim(\"Text\") <> '' AND char_length(\"Text\") <= 32000");
            table.HasCheckConstraint("CK_DocumentChunks_Pages", "(\"PageStart\" IS NULL AND \"PageEnd\" IS NULL) OR (\"PageStart\" IS NOT NULL AND \"PageEnd\" IS NOT NULL AND \"PageStart\" >= 1 AND \"PageEnd\" >= \"PageStart\")");
            table.HasCheckConstraint("CK_DocumentChunks_Model", $"\"EmbeddingModel\" = '{EmbeddingContract.Model}'");
            // Zero vectors have undefined cosine distance and cannot participate in cosine HNSW indexing.
            table.HasCheckConstraint("CK_DocumentChunks_Vector", "\"Embedding\" IS NULL OR vector_norm(\"Embedding\") > 0");
        });
        builder.HasKey(chunk => chunk.Id);
        builder.Property(chunk => chunk.Text).HasColumnType("text").IsRequired();
        builder.Property(chunk => chunk.Embedding).HasColumnType($"vector({EmbeddingContract.Dimensions})");
        builder.Property(chunk => chunk.EmbeddingModel).HasMaxLength(128).IsRequired();
        builder.HasOne(chunk => chunk.Document).WithMany(document => document.Chunks)
            .HasForeignKey(chunk => chunk.DocumentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(chunk => new { chunk.DocumentId, chunk.Position }).IsUnique();
        // This table is one compatible space. A future model switch must replace vectors and migrate the constraint/index.
        builder.HasIndex(chunk => chunk.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops")
            .HasFilter("\"Embedding\" IS NOT NULL");
    }
}
