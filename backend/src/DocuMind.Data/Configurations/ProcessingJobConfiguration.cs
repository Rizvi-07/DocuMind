using DocuMind.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocuMind.Data.Configurations;

/// <summary>Enforces retry/lease invariants and indexes queued and interrupted jobs for future worker polling.</summary>
public sealed class ProcessingJobConfiguration : IEntityTypeConfiguration<ProcessingJob>
{
    /// <summary>Optimistic xmin concurrency prevents two workers from persisting ownership of the same attempt.</summary>
    public void Configure(EntityTypeBuilder<ProcessingJob> builder)
    {
        builder.ToTable("ProcessingJobs", table =>
        {
            table.HasCheckConstraint("CK_ProcessingJobs_Status", ModelConstraints.EnumValues<ProcessingJobStatus>("Status"));
            table.HasCheckConstraint("CK_ProcessingJobs_Attempts", "\"MaxAttempts\" BETWEEN 1 AND 10 AND \"AttemptCount\" BETWEEN 0 AND \"MaxAttempts\"");
            table.HasCheckConstraint("CK_ProcessingJobs_FailureCode", ModelConstraints.EnumValues<ProcessingFailureCode>("LastFailureCode", true));
            table.HasCheckConstraint("CK_ProcessingJobs_Lease", "(\"Status\" = 'Running' AND \"LeaseId\" IS NOT NULL AND \"LeaseId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"LeaseExpiresAt\" IS NOT NULL AND \"StartedAt\" IS NOT NULL AND \"LeaseExpiresAt\" > \"StartedAt\" AND \"AttemptCount\" > 0) OR (\"Status\" <> 'Running' AND \"LeaseId\" IS NULL AND \"LeaseExpiresAt\" IS NULL)");
            table.HasCheckConstraint("CK_ProcessingJobs_Completion", "(\"Status\" IN ('Succeeded', 'Failed')) = (\"CompletedAt\" IS NOT NULL)");
            table.HasCheckConstraint("CK_ProcessingJobs_Failed", "\"Status\" <> 'Failed' OR \"LastFailureCode\" IS NOT NULL");
        });
        builder.HasKey(job => job.Id);
        builder.Property(job => job.Status).HasConversion<string>().HasMaxLength(24).HasDefaultValue(ProcessingJobStatus.Queued);
        builder.Property(job => job.LastFailureCode).HasConversion<string>().HasMaxLength(32);
        builder.Ignore(job => job.LastFailureMessage);
        builder.Property(job => job.MaxAttempts).HasDefaultValue(5);
        builder.Property(job => job.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Property(job => job.AvailableAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Property(job => job.Version).IsRowVersion().HasColumnName("xmin");
        builder.HasOne(job => job.Document).WithMany(document => document.ProcessingJobs)
            .HasForeignKey(job => job.DocumentId).OnDelete(DeleteBehavior.Cascade);
        // One queued/running ingestion request per document; terminal history does not block a new request.
        builder.HasIndex(job => job.DocumentId).IsUnique().HasFilter("\"Status\" IN ('Queued', 'Running')");
        builder.HasIndex(job => new { job.AvailableAt, job.CreatedAt }).HasFilter("\"Status\" = 'Queued'");
        builder.HasIndex(job => job.LeaseExpiresAt).HasFilter("\"Status\" = 'Running'");
    }
}
