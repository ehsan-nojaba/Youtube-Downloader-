using YtDownloader.Domain;

namespace YtDownloader.Domain.UnitTests;

public sealed class DownloadJobTests
{
    [Fact]
    public void NewJobIsQueued()
    {
        var job = NewJob();
        Assert.NotEqual(Guid.Empty, job.Id);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(0, job.ProgressPercent);
        Assert.Equal("1080p", job.Quality);
        Assert.Equal(MediaFormat.Mp4, job.Format);
        Assert.Null(job.CompletedAt);
        Assert.Null(job.ExpiresAt);
        Assert.Null(job.FilePath);
        Assert.Null(job.ErrorMessage);
    }

    [Fact]
    public void ProcessingJobCanReportProgressAndComplete()
    {
        var job = NewJob();
        job.Start();
        Assert.Equal(JobStatus.Processing, job.Status);
        job.ReportProgress(50);
        Assert.Equal(50, job.ProgressPercent);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        job.Complete("video.mp4", expiresAt);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(100, job.ProgressPercent);
        Assert.Equal("video.mp4", job.FilePath);
        Assert.NotNull(job.CompletedAt);
        Assert.Equal(expiresAt, job.ExpiresAt);
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Processing)]
    public void ActiveJobCanFail(JobStatus status)
    {
        var job = InStatus(status);
        job.Fail("Download failed");
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("Download failed", job.ErrorMessage);
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    public void FinishedJobCanExpire(JobStatus status)
    {
        var job = InStatus(status);
        job.Expire();
        Assert.Equal(JobStatus.Expired, job.Status);
    }

    public static IEnumerable<object[]> InvalidTransitions()
    {
        foreach (var status in Enum.GetValues<JobStatus>())
        {
            if (status != JobStatus.Queued)
                yield return [status, "Start"];
            if (status != JobStatus.Processing)
            {
                yield return [status, "ReportProgress"];
                yield return [status, "Complete"];
            }
            if (status is not (JobStatus.Queued or JobStatus.Processing))
                yield return [status, "Fail"];
            if (status is not (JobStatus.Completed or JobStatus.Failed))
                yield return [status, "Expire"];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidTransitions))]
    public void InvalidTransitionThrowsWithoutChangingState(JobStatus status, string operation)
    {
        var job = InStatus(status);
        var progress = job.ProgressPercent;
        var path = job.FilePath;
        var error = job.ErrorMessage;
        var completed = job.CompletedAt;
        var expiration = job.ExpiresAt;

        Assert.Throws<DomainException>(() =>
        {
            switch (operation)
            {
                case "Start": job.Start(); break;
                case "ReportProgress": job.ReportProgress(50); break;
                case "Complete": job.Complete("video.mp4", DateTimeOffset.UtcNow.AddHours(1)); break;
                case "Fail": job.Fail("Failed"); break;
                case "Expire": job.Expire(); break;
            }
        });

        Assert.Equal(status, job.Status);
        Assert.Equal(progress, job.ProgressPercent);
        Assert.Equal(path, job.FilePath);
        Assert.Equal(error, job.ErrorMessage);
        Assert.Equal(completed, job.CompletedAt);
        Assert.Equal(expiration, job.ExpiresAt);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(49)]
    [InlineData(101)]
    public void InvalidProgressIsRejected(int progress)
    {
        var job = InStatus(JobStatus.Processing);
        job.ReportProgress(50);
        Assert.Throws<DomainException>(() => job.ReportProgress(progress));
        Assert.Equal(50, job.ProgressPercent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void ProgressBoundariesAreAccepted(int progress)
    {
        var job = InStatus(JobStatus.Processing);
        job.ReportProgress(progress);
        job.ReportProgress(progress);
        Assert.Equal(progress, job.ProgressPercent);
    }

    [Fact]
    public void CompletionWithPastExpirationIsRejected()
    {
        var job = InStatus(JobStatus.Processing);
        Assert.Throws<DomainException>(() => job.Complete("video.mp4", DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Equal(JobStatus.Processing, job.Status);
        Assert.Null(job.FilePath);
        Assert.Null(job.CompletedAt);
        Assert.Null(job.ExpiresAt);
    }

    private static DownloadJob NewJob() => new("https://www.youtube.com/watch?v=example", MediaFormat.Mp4, "1080p");

    private static DownloadJob InStatus(JobStatus status)
    {
        var job = NewJob();
        if (status is JobStatus.Processing or JobStatus.Completed or JobStatus.Expired)
            job.Start();
        if (status is JobStatus.Completed or JobStatus.Expired)
            job.Complete("video.mp4", DateTimeOffset.UtcNow.AddHours(1));
        if (status == JobStatus.Failed)
            job.Fail("Failed");
        if (status == JobStatus.Expired)
            job.Expire();
        return job;
    }
}
