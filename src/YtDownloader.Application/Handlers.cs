using MediatR;
using Microsoft.Extensions.Options;
using YtDownloader.Domain;

namespace YtDownloader.Application;

public sealed class GetVideoInfoQueryHandler(IYoutubeService youtube, IOptions<MediaOptions> options)
    : IRequestHandler<GetVideoInfoQuery, Result<VideoInfoDto>>
{
    public async Task<Result<VideoInfoDto>> Handle(GetVideoInfoQuery request, CancellationToken cancellationToken)
    {
        var result = await youtube.GetVideoInfoAsync(request.Url, cancellationToken);
        return result.IsSuccess && result.Value!.Duration > TimeSpan.FromMinutes(options.Value.MaxVideoDurationMinutes)
            ? Result<VideoInfoDto>.Failure(new("VideoTooLong", "The video exceeds the maximum allowed duration."))
            : result;
    }
}

public sealed class CreateDownloadJobCommandHandler(
    IYoutubeService youtube, IJobRepository jobs, IUnitOfWork unitOfWork, IJobQueue queue, IOptions<MediaOptions> options)
    : IRequestHandler<CreateDownloadJobCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateDownloadJobCommand request, CancellationToken cancellationToken)
    {
        var info = await youtube.GetVideoInfoAsync(request.Url, cancellationToken);
        if (!info.IsSuccess)
            return Result<Guid>.Failure(info.Error!);
        if (info.Value!.Duration > TimeSpan.FromMinutes(options.Value.MaxVideoDurationMinutes))
            return Result<Guid>.Failure(new("VideoTooLong", "The video exceeds the maximum allowed duration."));

        var qualities = request.Format == MediaFormat.Mp4 ? info.Value!.VideoQualities : info.Value!.AudioQualities;
        if (!qualities.Any(option => option.Format == request.Format && option.Label == request.Quality))
            return Result<Guid>.Failure(new Error("QualityUnavailable", "The selected quality is unavailable for this video."));

        var job = new DownloadJob(request.Url, request.Format, request.Quality, info.Value!.Title);
        await jobs.AddAsync(job, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        try
        {
            await queue.EnqueueAsync(job.Id, cancellationToken);
        }
        catch (Exception exception)
        {
            job.Fail("The job could not be queued.");
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw;
            return Result<Guid>.Failure(new Error("QueueUnavailable", job.ErrorMessage!));
        }
        return Result<Guid>.Success(job.Id);
    }
}

public sealed class GetJobStatusQueryHandler(IJobRepository jobs)
    : IRequestHandler<GetJobStatusQuery, Result<JobStatusDto>>
{
    public async Task<Result<JobStatusDto>> Handle(GetJobStatusQuery request, CancellationToken cancellationToken)
    {
        var job = await jobs.GetByIdAsync(request.JobId, cancellationToken);
        return job is null
            ? Result<JobStatusDto>.Failure(new Error("NotFound", "The job was not found."))
            : Result<JobStatusDto>.Success(JobStatusDto.FromJob(job));
    }
}

public sealed class GetDownloadFileQueryHandler(IJobRepository jobs, IFileStorage files)
    : IRequestHandler<GetDownloadFileQuery, Result<DownloadFileDto>>
{
    public async Task<Result<DownloadFileDto>> Handle(GetDownloadFileQuery request, CancellationToken cancellationToken)
    {
        var job = await jobs.GetByIdAsync(request.JobId, cancellationToken);
        if (job is null)
            return Result<DownloadFileDto>.Failure(new Error("NotFound", "The job was not found."));
        if (job.Status == JobStatus.Expired || job.ExpiresAt <= DateTimeOffset.UtcNow)
            return Result<DownloadFileDto>.Failure(new Error("Expired", "The download has expired."));
        if (job.Status != JobStatus.Completed || job.FilePath is null)
            return Result<DownloadFileDto>.Failure(new Error("NotReady", "The download is not ready."));

        var file = await files.OpenReadAsync(job.FilePath, cancellationToken);
        if (!file.IsSuccess)
            return file;
        return Result<DownloadFileDto>.Success(file.Value! with
        {
            FileName = $"{job.Id}.{(job.Format == MediaFormat.Mp4 ? "mp4" : "mp3")}",
            ContentType = job.Format == MediaFormat.Mp4 ? "video/mp4" : "audio/mpeg"
        });
    }
}
