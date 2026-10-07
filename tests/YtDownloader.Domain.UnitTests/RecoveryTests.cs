using YtDownloader.Domain;

namespace YtDownloader.Domain.UnitTests;

public sealed class RecoveryTests
{
    [Fact]
    public void InterruptedJobCanBeRequeuedWithProgressReset()
    {
        var job = new DownloadJob("https://youtu.be/dQw4w9WgXcQ", MediaFormat.Mp4, "1080p");
        job.Start();
        job.ReportProgress(80);
        job.Requeue();
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(0, job.ProgressPercent);
        job.Start();
        Assert.Equal(JobStatus.Processing, job.Status);
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Expired)]
    public void TerminalJobsCannotBeRequeued(JobStatus status)
    {
        var job = new DownloadJob("https://youtu.be/dQw4w9WgXcQ", MediaFormat.Mp4, "1080p");
        if (status == JobStatus.Completed)
        {
            job.Start();
            job.Complete("file.mp4", DateTimeOffset.UtcNow.AddMinutes(30));
        }
        else
        {
            job.Fail("Failed");
            if (status == JobStatus.Expired) job.Expire();
        }
        Assert.Throws<DomainException>(() => job.Requeue());
        Assert.Equal(status, job.Status);
    }
}
