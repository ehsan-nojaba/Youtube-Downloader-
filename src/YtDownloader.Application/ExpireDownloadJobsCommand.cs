using MediatR;

namespace YtDownloader.Application;

public sealed record ExpireDownloadJobsCommand : IRequest<Result<int>>;

public sealed class ExpireDownloadJobsCommandHandler(
    IJobRepository jobs, IFileStorage files, IUnitOfWork unitOfWork,
    IProgressNotifier notifier, TimeProvider time) : IRequestHandler<ExpireDownloadJobsCommand, Result<int>>
{
    public async Task<Result<int>> Handle(ExpireDownloadJobsCommand request, CancellationToken cancellationToken)
    {
        var expired = await jobs.GetExpiredAsync(time.GetUtcNow(), cancellationToken);
        var failed = 0;
        foreach (var job in expired)
        {
            // Leave jobs eligible for retry when deletion fails.
            try
            {
                if (job.FilePath is { } path) await files.DeleteAsync(path, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed++;
                continue;
            }
            job.Expire();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await notifier.NotifyAsync(JobStatusDto.FromJob(job), cancellationToken);
        }
        return failed > 0
            ? Result<int>.Failure(new("CleanupFailed", $"{failed} files could not be deleted and will be retried."))
            : Result<int>.Success(expired.Count);
    }
}
