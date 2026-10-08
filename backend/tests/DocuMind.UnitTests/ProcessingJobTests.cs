using DocuMind.Data.Entities;
using Xunit;

namespace DocuMind.UnitTests;

/// <summary>Checks retry and lease decisions with a fixed clock, without a worker or database.</summary>
public sealed class ProcessingJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    /// <summary>A future retry must not be claimed early or consume its attempt budget.</summary>
    [Fact]
    public void FutureJobCannotBeClaimed()
    {
        var job = new ProcessingJob { AvailableAt = Now.AddMinutes(1) };
        Assert.False(job.TryClaim(Guid.NewGuid(), Now, LeaseDuration));
        Assert.Equal(0, job.AttemptCount);
        Assert.Equal(ProcessingJobStatus.Queued, job.Status);
    }

    /// <summary>Invalid lease parameters fail before changing the job.</summary>
    [Fact]
    public void ClaimRejectsEmptyIdentityAndNonpositiveDuration()
    {
        var job = new ProcessingJob { AvailableAt = Now };
        Assert.Throws<ArgumentException>(() => job.TryClaim(Guid.Empty, Now, LeaseDuration));
        Assert.Throws<ArgumentOutOfRangeException>(() => job.TryClaim(Guid.NewGuid(), Now, TimeSpan.Zero));
        Assert.Equal(0, job.AttemptCount);
    }

    /// <summary>Only the current, unexpired lease can heartbeat, complete, or report failure.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignOrExpiredLeaseCannotChangeRunningJob(bool expired)
    {
        var lease = Guid.NewGuid();
        var job = new ProcessingJob { AvailableAt = Now };
        Assert.True(job.TryClaim(lease, Now, LeaseDuration));
        var actor = expired ? lease : Guid.NewGuid();
        var clock = expired ? Now + LeaseDuration : Now;
        Assert.False(job.TryRenewLease(actor, clock, LeaseDuration));
        Assert.False(job.TryComplete(actor, clock));
        Assert.False(job.TryFail(actor, clock, ProcessingFailureCode.ExtractionFailed, TimeSpan.Zero));
        Assert.Equal(ProcessingJobStatus.Running, job.Status);
    }

    /// <summary>A successful heartbeat extends the window in which the same worker can finish.</summary>
    [Fact]
    public void RenewedLeaseCanCompleteAfterOriginalDeadline()
    {
        var lease = Guid.NewGuid();
        var job = new ProcessingJob { AvailableAt = Now };
        Assert.True(job.TryClaim(lease, Now, LeaseDuration));
        Assert.True(job.TryRenewLease(lease, Now.AddSeconds(30), LeaseDuration));
        Assert.True(job.TryComplete(lease, Now.AddSeconds(70)));
        Assert.Equal(ProcessingJobStatus.Succeeded, job.Status);
        Assert.Null(job.LeaseId);
        Assert.NotNull(job.CompletedAt);
        Assert.False(job.TryClaim(Guid.NewGuid(), Now.AddMinutes(3), LeaseDuration));
    }

    /// <summary>Retry backoff is respected and repeated failures eventually become terminal.</summary>
    [Fact]
    public void FailuresRespectBackoffAndAttemptLimit()
    {
        var job = new ProcessingJob { AvailableAt = Now, MaxAttempts = 2 };
        var lease = Guid.NewGuid();
        Assert.True(job.TryClaim(lease, Now, LeaseDuration));
        Assert.True(job.TryFail(lease, Now, ProcessingFailureCode.EmbeddingFailed, TimeSpan.FromMinutes(2)));
        Assert.Equal(ProcessingJobStatus.Queued, job.Status);
        Assert.Null(job.CompletedAt);
        Assert.False(job.TryClaim(Guid.NewGuid(), Now.AddMinutes(1), LeaseDuration));
        lease = Guid.NewGuid();
        Assert.True(job.TryClaim(lease, Now.AddMinutes(2), LeaseDuration));
        Assert.True(job.TryFail(lease, Now.AddMinutes(2), ProcessingFailureCode.EmbeddingFailed, TimeSpan.Zero));
        Assert.Equal(ProcessingJobStatus.Failed, job.Status);
        Assert.Equal(2, job.AttemptCount);
        Assert.NotNull(job.CompletedAt);
        Assert.Null(job.LeaseId);
        Assert.False(job.TryClaim(Guid.NewGuid(), Now.AddMinutes(3), LeaseDuration));
    }

    /// <summary>Crash recovery consumes attempts and fences a stalled worker after reassignment.</summary>
    [Theory]
    [InlineData(1, ProcessingJobStatus.Failed)]
    [InlineData(2, ProcessingJobStatus.Queued)]
    public void ExpiredLeaseRecoversWithinRetryBudget(int limit, ProcessingJobStatus expected)
    {
        var oldLease = Guid.NewGuid();
        var job = new ProcessingJob { AvailableAt = Now, MaxAttempts = limit };
        Assert.True(job.TryClaim(oldLease, Now, LeaseDuration));
        Assert.False(job.RecoverExpiredLease(Now.AddSeconds(59)));
        Assert.True(job.RecoverExpiredLease(Now + LeaseDuration));
        Assert.Equal(expected, job.Status);
        Assert.Equal(ProcessingFailureCode.WorkerInterrupted, job.LastFailureCode);
        Assert.Null(job.LeaseId);
        if (expected == ProcessingJobStatus.Queued)
        {
            Assert.True(job.TryClaim(Guid.NewGuid(), Now + LeaseDuration, LeaseDuration));
            Assert.False(job.TryComplete(oldLease, Now + LeaseDuration));
            Assert.Equal(2, job.AttemptCount);
        }
    }

    /// <summary>Invalid failure codes and negative delays cannot enter persisted retry state.</summary>
    [Fact]
    public void FailureRejectsInvalidCodeAndDelay()
    {
        var lease = Guid.NewGuid();
        var job = new ProcessingJob { AvailableAt = Now };
        job.TryClaim(lease, Now, LeaseDuration);
        Assert.Throws<ArgumentOutOfRangeException>(() => job.TryFail(lease, Now, (ProcessingFailureCode)999, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => job.TryFail(lease, Now, ProcessingFailureCode.ProcessingFailed, TimeSpan.FromSeconds(-1)));
        Assert.Equal(ProcessingJobStatus.Running, job.Status);
    }

    /// <summary>Public failure information is constant allowlisted text, never an exception or storage path.</summary>
    [Fact]
    public void FailureMessagesAreSafeAndNullWhenNoFailureExists()
    {
        Assert.Null(ProcessingFailures.GetSafeMessage(null));
        Assert.Equal(ProcessingFailures.GetSafeMessage(ProcessingFailureCode.ProcessingFailed),
            ProcessingFailures.GetSafeMessage((ProcessingFailureCode)999));
        foreach (var code in Enum.GetValues<ProcessingFailureCode>())
            Assert.False(string.IsNullOrWhiteSpace(ProcessingFailures.GetSafeMessage(code)));
    }
}
