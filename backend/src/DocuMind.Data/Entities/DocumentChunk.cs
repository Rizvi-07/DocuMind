using Pgvector;

namespace DocuMind.Data.Entities;

/// <summary>Stores ordered extracted text and a vector in the explicitly selected embedding search space.</summary>
public sealed class DocumentChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    /// <summary>Zero-based position, unique within the source document, for deterministic citations and reconstruction.</summary>
    public int Position { get; set; }
    public string Text { get; set; } = string.Empty;
    /// <summary>One-based inclusive page range; both values are null when the source has no page metadata.</summary>
    public int? PageStart { get; set; }
    public int? PageEnd { get; set; }
    /// <summary>Null until embedding succeeds; PostgreSQL vector(1536) enforces output dimensions.</summary>
    public Vector? Embedding { get; set; }
    /// <summary>Every row has this identity, including pending chunks, so incompatible models cannot share this space.</summary>
    public string EmbeddingModel { get; set; } = EmbeddingContract.Model;
}
