using Microsoft.Extensions.Options;
using NSubstitute;
using YtDownloader.Application;
using YtDownloader.Domain;

namespace YtDownloader.Application.UnitTests;

public sealed class HandlerTests
{
    internal const string Url = "https://youtube.com/watch?v=dQw4w9WgXcQ";
    private readonly IYoutubeService youtube = Substitute.For<IYoutubeService>();
    private readonly IJobRepository jobs = Substitute.For<IJobRepository>();
    private readonly IUnitOfWork unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IJobQueue queue = Substitute.For<IJobQueue>();
    private readonly IFileStorage files = Substitute.For<IFileStorage>();
    private readonly IMediaConverter converter = Substitute.For<IMediaConverter>();
    private readonly IProgressNotifier notifier = Substitute.For<IProgressNotifier>();
    private static readonly VideoInfoDto Info = new("Title", "Author", "https://example.com/thumb.jpg", TimeSpan.FromMinutes(3),
        [new("1080p", MediaFormat.Mp4, null, "1920x1080", 1000)],
        [new("192k", MediaFormat.Mp3, 192000, null, 500)]);

    [Fact]
    public async Task VideoInfoForwardsUrlAndCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        youtube.GetVideoInfoAsync(Url, cancellation.Token).Returns(Result<VideoInfoDto>.Success(Info));
        var result = await new GetVideoInfoQueryHandler(youtube, Options.Create(new MediaOptions())).Handle(new(Url), cancellation.Token);
        Assert.Same(Info, result.Value);
        await youtube.Received(1).GetVideoInfoAsync(Url, cancellation.Token);
    }

    [Theory]
    [InlineData(MediaFormat.Mp4, "1080p")]
    [InlineData(MediaFormat.Mp3, "192k")]
    public async Task CreationPersistsBeforeEnqueueing(MediaFormat format, string quality)
    {
        youtube.GetVideoInfoAsync(Url, Arg.Any<CancellationToken>()).Returns(Result<VideoInfoDto>.Success(Info));
        var result = await CreateHandler().Handle(new(Url, format, quality), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Received.InOrder(() =>
        {
            jobs.AddAsync(Arg.Is<DownloadJob>(job => job.Id == result.Value && job.VideoTitle == "Title" &&
                job.Format == format && job.Quality == quality && job.Status == JobStatus.Queued), CancellationToken.None);
            unitOfWork.SaveChangesAsync(CancellationToken.None);
            queue.EnqueueAsync(result.Value, CancellationToken.None);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationDoesNotPersistWhenInfoFailsOrQualityUnavailable(bool unavailable)
    {
        youtube.GetVideoInfoAsync(Url, Arg.Any<CancellationToken>()).Returns(unavailable
            ? Result<VideoInfoDto>.Success(Info)
            : Result<VideoInfoDto>.Failure(new("YoutubeUnavailable", "Unavailable")));
        var result = await CreateHandler().Handle(new(Url, MediaFormat.Mp4, "720p"), CancellationToken.None);
        Assert.Equal(unavailable ? "QualityUnavailable" : "YoutubeUnavailable", result.Error!.Code);
        await jobs.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await queue.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default);
    }

    [Fact]
    public async Task QueueFailureMarksPersistedJobFailed()
    {
        DownloadJob? saved = null;
        youtube.GetVideoInfoAsync(Url, Arg.Any<CancellationToken>()).Returns(Result<VideoInfoDto>.Success(Info));
        jobs.AddAsync(Arg.Any<DownloadJob>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            saved = call.Arg<DownloadJob>();
            return Task.CompletedTask;
        });
        queue.EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("offline")));
        var result = await CreateHandler().Handle(new(Url, MediaFormat.Mp4, "1080p"), CancellationToken.None);
        Assert.Equal("QueueUnavailable", result.Error!.Code);
        Assert.Equal(JobStatus.Failed, saved!.Status);
        await unitOfWork.Received(2).SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StatusReturnsSnapshotWithoutFilePath()
    {
        var job = SetupJob();
        var result = await new GetJobStatusQueryHandler(jobs).Handle(new(job.Id), CancellationToken.None);
        Assert.Equal(job.Id, result.Value!.Id);
        Assert.Equal(JobStatus.Queued, result.Value.Status);
    }

    [Fact]
    public async Task MissingJobsReturnNotFound()
    {
        var id = Guid.NewGuid();
        Assert.Equal("NotFound", (await new GetJobStatusQueryHandler(jobs).Handle(new(id), default)).Error!.Code);
        Assert.Equal("NotFound", (await new GetDownloadFileQueryHandler(jobs, files).Handle(new(id), default)).Error!.Code);
        Assert.Equal("NotFound", (await ProcessHandler().Handle(new(id), default)).Error!.Code);
    }

    [Theory]
    [InlineData(MediaFormat.Mp4, "video/mp4", ".mp4")]
    [InlineData(MediaFormat.Mp3, "audio/mpeg", ".mp3")]
    public async Task CompletedDownloadReturnsStreamAndSafeMetadata(MediaFormat format, string contentType, string extension)
    {
        var job = SetupJob(format);
        job.Start();
        job.Complete("output", DateTimeOffset.UtcNow.AddHours(1));
        using var stream = new MemoryStream([1, 2, 3]);
        files.OpenReadAsync("output", Arg.Any<CancellationToken>())
            .Returns(Result<DownloadFileDto>.Success(new(stream, "unsafe name", "unknown")));
        var result = await new GetDownloadFileQueryHandler(jobs, files).Handle(new(job.Id), default);
        Assert.Same(stream, result.Value!.Stream);
        Assert.Equal(job.Id + extension, result.Value.FileName);
        Assert.Equal(contentType, result.Value.ContentType);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(JobStatus.Queued, "NotReady")]
    [InlineData(JobStatus.Processing, "NotReady")]
    [InlineData(JobStatus.Failed, "NotReady")]
    [InlineData(JobStatus.Expired, "Expired")]
    public async Task UnavailableDownloadDoesNotOpenFile(JobStatus status, string error)
    {
        var job = SetupJob();
        if (status == JobStatus.Processing) job.Start();
        if (status is JobStatus.Failed or JobStatus.Expired) job.Fail("Failed");
        if (status == JobStatus.Expired) job.Expire();
        var result = await new GetDownloadFileQueryHandler(jobs, files).Handle(new(job.Id), default);
        Assert.Equal(error, result.Error!.Code);
        await files.DidNotReceiveWithAnyArgs().OpenReadAsync(default!, default);
    }

    [Fact]
    public async Task MissingStoredFileErrorIsReturned()
    {
        var job = SetupJob();
        job.Start();
        job.Complete("output", DateTimeOffset.UtcNow.AddHours(1));
        files.OpenReadAsync("output", Arg.Any<CancellationToken>())
            .Returns(Result<DownloadFileDto>.Failure(new("FileNotFound", "Missing")));
        Assert.Equal("FileNotFound", (await new GetDownloadFileQueryHandler(jobs, files).Handle(new(job.Id), default)).Error!.Code);
    }

    [Fact]
    public async Task ProcessingCompletesPersistsProgressNotifiesAndCleansSource()
    {
        var job = SetupProcessing();
        converter.ConvertAsync(new MediaSourceDto(null, "source", TimeSpan.FromMinutes(1)), "output", job.Format, job.Quality,
            Arg.Any<Func<int, CancellationToken, Task>>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await call.Arg<Func<int, CancellationToken, Task>>()(50, call.Arg<CancellationToken>());
            return Result<bool>.Success(true);
        });
        var result = await ProcessHandler().Handle(new(job.Id), default);
        Assert.True(result.IsSuccess);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(100, job.ProgressPercent);
        Assert.Equal("output", job.FilePath);
        Assert.InRange(job.ExpiresAt!.Value - job.CompletedAt!.Value, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
        await unitOfWork.Received(3).SaveChangesAsync(default);
        await notifier.Received().NotifyAsync(Arg.Is<JobStatusDto>(dto => dto.Status == JobStatus.Processing && dto.ProgressPercent == 50), default);
        await notifier.Received().NotifyAsync(Arg.Is<JobStatusDto>(dto => dto.Status == JobStatus.Completed), default);
        await files.Received(1).DeleteAsync("source", default);
        await files.DidNotReceive().DeleteAsync("output", default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessingFailuresAreSavedAndPartialFilesCleaned(bool conversionFailure)
    {
        var job = SetupProcessing();
        var error = new Error("MediaUnavailable", "Cannot process");
        if (conversionFailure)
            converter.ConvertAsync(Arg.Any<MediaSourceDto>(), Arg.Any<string>(), Arg.Any<MediaFormat>(), Arg.Any<string>(),
                Arg.Any<Func<int, CancellationToken, Task>>(), Arg.Any<CancellationToken>()).Returns(Result<bool>.Failure(error));
        else
            youtube.DownloadAsync(Url, job.Format, job.Quality, Arg.Any<CancellationToken>()).Returns(Result<MediaSourceDto>.Failure(error));
        var result = await ProcessHandler().Handle(new(job.Id), default);
        Assert.Same(error, result.Error);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(error.Message, job.ErrorMessage);
        await notifier.Received().NotifyAsync(Arg.Is<JobStatusDto>(dto => dto.Status == JobStatus.Failed), default);
        if (conversionFailure)
        {
            await files.Received().DeleteAsync("source", default);
            await files.Received().DeleteAsync("output", default);
        }
        else
            await converter.DidNotReceiveWithAnyArgs().ConvertAsync(default!, default!, default, default!, default!, default);
    }

    [Fact]
    public async Task UnexpectedConversionFailureDoesNotExposeInternalDetails()
    {
        var job = SetupProcessing();
        converter.ConvertAsync(Arg.Any<MediaSourceDto>(), Arg.Any<string>(), Arg.Any<MediaFormat>(), Arg.Any<string>(),
            Arg.Any<Func<int, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result<bool>>(new IOException("secret internal path")));
        var result = await ProcessHandler().Handle(new(job.Id), default);
        Assert.Equal("ProcessingFailed", result.Error!.Code);
        Assert.DoesNotContain("secret", job.ErrorMessage!);
        Assert.Equal(JobStatus.Failed, job.Status);
        await files.Received().DeleteAsync("output", default);
    }

    [Fact]
    public async Task FailedMp4ConversionCleansBothSourceStreams()
    {
        var job = SetupProcessing();
        youtube.DownloadAsync(Url, job.Format, job.Quality, Arg.Any<CancellationToken>())
            .Returns(Result<MediaSourceDto>.Success(new("video", "audio", TimeSpan.FromMinutes(1))));
        converter.ConvertAsync(Arg.Any<MediaSourceDto>(), Arg.Any<string>(), Arg.Any<MediaFormat>(), Arg.Any<string>(),
            Arg.Any<Func<int, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Failure(new("ConversionFailed", "Failed")));
        await ProcessHandler().Handle(new(job.Id), default);
        await files.Received(1).DeleteAsync("video", default);
        await files.Received(1).DeleteAsync("audio", default);
        await files.Received(1).DeleteAsync("output", default);
    }

    [Fact]
    public async Task FailedMp3ConversionCleansThumbnailAndAudio()
    {
        var job = SetupJob(MediaFormat.Mp3);
        youtube.DownloadAsync(Url, job.Format, job.Quality, Arg.Any<CancellationToken>())
            .Returns(Result<MediaSourceDto>.Success(new(null, "audio", TimeSpan.FromMinutes(1), "thumbnail")));
        files.GetOutputPath(job.Id, job.Format).Returns("output");
        converter.ConvertAsync(Arg.Any<MediaSourceDto>(), Arg.Any<string>(), Arg.Any<MediaFormat>(), Arg.Any<string>(),
            Arg.Any<Func<int, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Failure(new("ConversionFailed", "Failed")));
        await ProcessHandler().Handle(new(job.Id), default);
        await files.Received(1).DeleteAsync("thumbnail", default);
        await files.Received(1).DeleteAsync("audio", default);
    }

    [Fact]
    public async Task CancellationMarksJobFailedCleansFilesAndPropagates()
    {
        var job = SetupProcessing();
        using var cancellation = new CancellationTokenSource();
        converter.ConvertAsync(Arg.Any<MediaSourceDto>(), Arg.Any<string>(), Arg.Any<MediaFormat>(), Arg.Any<string>(),
            Arg.Any<Func<int, CancellationToken, Task>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<Result<bool>>(cancellation.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessHandler().Handle(new(job.Id), cancellation.Token));
        Assert.Equal(JobStatus.Failed, job.Status);
        await unitOfWork.Received().SaveChangesAsync(CancellationToken.None);
        await files.Received().DeleteAsync("source", CancellationToken.None);
        await files.Received().DeleteAsync("output", CancellationToken.None);
    }

    [Fact]
    public async Task ProcessingRejectsAlreadyStartedJob()
    {
        var job = SetupJob();
        job.Start();
        Assert.Equal("InvalidStatus", (await ProcessHandler().Handle(new(job.Id), default)).Error!.Code);
        await youtube.DidNotReceiveWithAnyArgs().DownloadAsync(default!, default, default!, default);
    }

    private DownloadJob SetupJob(MediaFormat format = MediaFormat.Mp4)
    {
        var job = new DownloadJob(Url, format, format == MediaFormat.Mp4 ? "1080p" : "192k", "Title");
        jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        return job;
    }

    private DownloadJob SetupProcessing()
    {
        var job = SetupJob();
        youtube.DownloadAsync(Url, job.Format, job.Quality, Arg.Any<CancellationToken>()).Returns(Result<MediaSourceDto>.Success(new(null, "source", TimeSpan.FromMinutes(1))));
        files.GetOutputPath(job.Id, job.Format).Returns("output");
        return job;
    }

    private CreateDownloadJobCommandHandler CreateHandler() => new(youtube, jobs, unitOfWork, queue, Options.Create(new MediaOptions()));
    private ProcessDownloadJobCommandHandler ProcessHandler() => new(jobs, youtube, converter, files, unitOfWork, notifier, Options.Create(new MediaOptions()));
}




