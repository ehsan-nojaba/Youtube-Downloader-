using YtDownloader.Domain;

namespace YtDownloader.Application;

public interface IYoutubeService
{
    Task<Result<VideoInfoDto>> GetVideoInfoAsync(string url, CancellationToken cancellationToken);
    // Returns temporary source files owned by the caller. Progress is normalized to 0..1.
    Task<Result<MediaSourceDto>> DownloadAsync(string url, MediaFormat format, string quality,
        CancellationToken cancellationToken, IProgress<double>? progress = null);
}

public interface IMediaConverter
{
    Task<Result<bool>> ConvertAsync(
        MediaSourceDto source, string outputPath, MediaFormat format, string quality,
        Func<int, CancellationToken, Task> reportProgress, CancellationToken cancellationToken);
}

public interface IFileStorage
{
    string GetOutputPath(Guid jobId, MediaFormat format);
    Task<Result<DownloadFileDto>> OpenReadAsync(string path, CancellationToken cancellationToken);
    Task DeleteAsync(string path, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken);
    Task<Result<string>> SaveAsync(string fileName, Stream source, CancellationToken cancellationToken);
}

public interface IJobRepository
{
    // Returned entities are tracked; saves are committed through IUnitOfWork.
    Task<DownloadJob?> GetByIdAsync(Guid jobId, CancellationToken cancellationToken);
    Task AddAsync(DownloadJob job, CancellationToken cancellationToken);
    Task<IReadOnlyList<DownloadJob>> GetExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<DownloadJob>> GetByIdsAsync(Guid[] jobIds, CancellationToken cancellationToken);
}

public interface IJobQueue
{
    Task EnqueueAsync(Guid jobId, CancellationToken cancellationToken);
    Task<Guid> DequeueAsync(CancellationToken cancellationToken);
}

public interface IProgressNotifier
{
    Task NotifyAsync(JobStatusDto job, CancellationToken cancellationToken);
}

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
