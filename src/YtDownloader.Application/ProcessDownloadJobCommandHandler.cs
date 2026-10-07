using MediatR;
using Microsoft.Extensions.Options;
using YtDownloader.Domain;

namespace YtDownloader.Application;

public sealed class ProcessDownloadJobCommandHandler(
    IJobRepository jobs, IYoutubeService youtube, IMediaConverter converter,
    IFileStorage files, IUnitOfWork unitOfWork, IProgressNotifier notifier, IOptions<MediaOptions> options)
    : IRequestHandler<ProcessDownloadJobCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(ProcessDownloadJobCommand request, CancellationToken cancellationToken)
    {
        var job = await jobs.GetByIdAsync(request.JobId, cancellationToken);
        if (job is null)
            return Result<bool>.Failure(new Error("NotFound", "The job was not found."));
        if (job.Status != JobStatus.Queued)
            return Result<bool>.Failure(new Error("InvalidStatus", "Only queued jobs can be processed."));

        job.Start();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        MediaSourceDto? mediaSource = null;
        string? outputPath = null;
        try
        {
            await notifier.NotifyAsync(JobStatusDto.FromJob(job), cancellationToken);
            var source = await youtube.DownloadAsync(job.Url, job.Format, job.Quality, cancellationToken);
            if (!source.IsSuccess)
                return await FailAsync(source.Error!, cancellationToken);
            mediaSource = source.Value!;
            outputPath = files.GetOutputPath(job.Id, job.Format);
            var conversion = await converter.ConvertAsync(
                mediaSource, outputPath, job.Format, job.Quality,
                async (percent, token) =>
                {
                    job.ReportProgress(percent);
                    await unitOfWork.SaveChangesAsync(token);
                    await notifier.NotifyAsync(JobStatusDto.FromJob(job), token);
                }, cancellationToken);
            if (!conversion.IsSuccess)
                return await FailAsync(conversion.Error!, cancellationToken);

            job.Complete(outputPath, DateTimeOffset.UtcNow.AddMinutes(options.Value.FileRetentionMinutes));
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await notifier.NotifyAsync(JobStatusDto.FromJob(job), cancellationToken);
            return Result<bool>.Success(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (job.Status == JobStatus.Processing)
                await FailAsync(new Error("Cancelled", "Processing was cancelled."), CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            // Unexpected infrastructure failures become safe public errors.
            if (job.Status != JobStatus.Processing)
                throw;
            return await FailAsync(new Error("ProcessingFailed", "Media processing failed."), CancellationToken.None);
        }
        finally
        {
            if (mediaSource is not null)
                foreach (var path in mediaSource.Paths)
                    await files.DeleteAsync(path, CancellationToken.None);
            if (outputPath is not null && job.Status != JobStatus.Completed)
                await files.DeleteAsync(outputPath, CancellationToken.None);
        }

        async Task<Result<bool>> FailAsync(Error error, CancellationToken token)
        {
            job.Fail(error.Message);
            await unitOfWork.SaveChangesAsync(token);
            await notifier.NotifyAsync(JobStatusDto.FromJob(job), token);
            return Result<bool>.Failure(error);
        }
    }
}
