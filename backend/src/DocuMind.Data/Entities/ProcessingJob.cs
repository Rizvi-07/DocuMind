namespace DocuMind.Data.Entities;

/// <summary>Persists a retryable document-ingestion request and a fenced, renewable worker lease.</summary>
public sealed class ProcessingJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public ProcessingJobStatus Status { get; private set; } = ProcessingJobStatus.Queued;
    /// <summary>Counts claims, including attempts whose worker crashed; the retry budget cannot grow forever.</summary>
    public int AttemptCount { get; private set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset AvailableAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    /// <summary>A new unpredictable value for each claim; stale workers cannot complete a replacement worker's lease.</summary>
    public Guid? LeaseId { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public ProcessingFailureCode? LastFailureCode { get; private set; }
    public string? LastFailureMessage => ProcessingFailures.GetSafeMessage(LastFailureCode);
    /// <summary>Mapped to PostgreSQL xmin: concurrent claims/heartbeats/completions conflict at SaveChanges.</summary>
    public uint Version { get; private set; }

    /// <summary>Claims a due queued attempt. Persist with optimistic concurrency before doing external processing work.</summary>
    public bool TryClaim(Guid leaseId, DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (leaseId == Guid.Empty) throw new ArgumentException("A nonempty lease ID is required.", nameof(leaseId));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (Status != ProcessingJobStatus.Queued || AvailableAt > now || AttemptCount >= MaxAttempts) return false;
        Status = ProcessingJobStatus.Running;
        AttemptCount++;
        StartedAt = now;
        LeaseId = leaseId;
        LeaseExpiresAt = now + leaseDuration;
        return true;
    }

    /// <summary>Extends only the current unexpired lease; the worker must save the heartbeat before the lease deadline.</summary>
    public bool TryRenewLease(Guid leaseId, DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (!OwnsLease(leaseId, now)) return false;
        LeaseExpiresAt = now + leaseDuration;
        return true;
    }

    /// <summary>Completes only an active owned attempt; write chunks/document status and this transition in one transaction.</summary>
    public bool TryComplete(Guid leaseId, DateTimeOffset now)
    {
        if (!OwnsLease(leaseId, now)) return false;
        Status = ProcessingJobStatus.Succeeded;
        CompletedAt = now;
        ClearLease();
        return true;
    }

    /// <summary>Schedules a bounded retry or records terminal failure using only a safe allowlisted failure code.</summary>
    public bool TryFail(Guid leaseId, DateTimeOffset now, ProcessingFailureCode failure, TimeSpan retryDelay)
    {
        if (!Enum.IsDefined(failure)) throw new ArgumentOutOfRangeException(nameof(failure));
        if (retryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));
        if (!OwnsLease(leaseId, now)) return false;
        LastFailureCode = failure;
        FinishOrRetry(now, retryDelay);
        return true;
    }

    /// <summary>A recovery sweep requeues expired leases; exhausted crash attempts become terminal instead of staying stuck.</summary>
    public bool RecoverExpiredLease(DateTimeOffset now)
    {
        if (Status != ProcessingJobStatus.Running || LeaseExpiresAt > now) return false;
        LastFailureCode = ProcessingFailureCode.WorkerInterrupted;
        FinishOrRetry(now, TimeSpan.Zero);
        return true;
    }

    /// <summary>Checks both lease identity and expiry so a stalled worker cannot publish results after losing ownership.</summary>
    private bool OwnsLease(Guid leaseId, DateTimeOffset now)
        => Status == ProcessingJobStatus.Running && LeaseId == leaseId && LeaseExpiresAt > now;

    /// <summary>Clears lease data and preserves the last failure across retry attempts for safe operator feedback.</summary>
    private void FinishOrRetry(DateTimeOffset now, TimeSpan delay)
    {
        Status = AttemptCount >= MaxAttempts ? ProcessingJobStatus.Failed : ProcessingJobStatus.Queued;
        CompletedAt = Status == ProcessingJobStatus.Failed ? now : null;
        AvailableAt = now + delay;
        ClearLease();
    }

    /// <summary>Non-running states must not retain a lease that appears claimable by a stale worker.</summary>
    private void ClearLease() { LeaseId = null; LeaseExpiresAt = null; }
}
