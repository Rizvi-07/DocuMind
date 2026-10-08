using DocuMind.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DocuMind.Data;

public sealed class DocuMindDbContext
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public DocuMindDbContext(
        DbContextOptions<DocuMindDbContext> options)
        : base(options)
    {
    }

    /// <summary>User-owned uploads; every request query must filter by the authenticated owner.</summary>
    public DbSet<Document> Documents => Set<Document>();
    /// <summary>Extracted chunks inherit access through their parent document.</summary>
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    /// <summary>Durable ingestion state for bounded retries and interrupted-worker recovery.</summary>
    public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();
    /// <summary>User-owned conversations, optionally scoped to a document.</summary>
    public DbSet<Conversation> Conversations => Set<Conversation>();
    /// <summary>Chat turns inherit access through their parent conversation.</summary>
    public DbSet<Message> Messages => Set<Message>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("vector");

        // Keep entity relationships, checks, and indexes in focused Data configuration classes.
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(DocuMindDbContext).Assembly);
    }
}