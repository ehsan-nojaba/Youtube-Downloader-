using NSubstitute;
using YtDownloader.Application;
using YtDownloader.Domain;

namespace YtDownloader.Application.UnitTests;

public sealed class CleanupTests
{
    [Fact]
    public async Task DeleteFailureLeavesJobRetryableAndOtherFilesAreStillCleaned()
    {
        var jobs = Substitute.For<IJobRepository>();
        var files = Substitute.For<IFileStorage>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var notifier = Substitute.For<IProgressNotifier>();
        var blocked = Completed("blocked");
        var good = Completed("good");
        jobs.GetExpiredAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { blocked, good });
        files.DeleteAsync("blocked", Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("locked")));
        var result = await new ExpireDownloadJobsCommandHandler(jobs, files, unitOfWork, notifier, TimeProvider.System)
            .Handle(new(), default);
        Assert.Equal("CleanupFailed", result.Error!.Code);
        Assert.Equal(JobStatus.Completed, blocked.Status);
        Assert.Equal("blocked", blocked.FilePath);
        Assert.Equal(JobStatus.Expired, good.Status);
        await unitOfWork.Received(1).SaveChangesAsync(default);
        await notifier.Received(1).NotifyAsync(Arg.Is<JobStatusDto>(job => job.Id == good.Id), default);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task HistoryRejectsInvalidBatchSize(int count)
    {
        var result = await new GetJobHistoryQueryValidator().ValidateAsync(new GetJobHistoryQuery(
            Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray()));
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task HistoryRejectsDuplicateEmptyAndNullIds()
    {
        var validator = new GetJobHistoryQueryValidator();
        var id = Guid.NewGuid();
        Assert.False((await validator.ValidateAsync(new GetJobHistoryQuery([id, id]))).IsValid);
        Assert.False((await validator.ValidateAsync(new GetJobHistoryQuery([Guid.Empty]))).IsValid);
        Assert.False((await validator.ValidateAsync(new GetJobHistoryQuery(null!))).IsValid);
    }

    private static DownloadJob Completed(string path)
    {
        var job = new DownloadJob("https://youtu.be/dQw4w9WgXcQ", MediaFormat.Mp4, "1080p");
        job.Start();
        job.Complete(path, DateTimeOffset.UtcNow.AddMinutes(1));
        return job;
    }
}
