using YtDownloader.Domain;

namespace YtDownloader.Application;

public sealed record QualityOptionDto(
    string Label, MediaFormat Format, long? Bitrate, string? Resolution, long? EstimatedSizeBytes);

public sealed record VideoInfoDto(
    string Title, string Author, string ThumbnailUrl, TimeSpan Duration,
    QualityOptionDto[] VideoQualities, QualityOptionDto[] AudioQualities);

public sealed record JobStatusDto(
    Guid Id, string? VideoTitle, MediaFormat Format, string Quality, JobStatus Status,
    int ProgressPercent, string? ErrorMessage, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, DateTimeOffset? ExpiresAt)
{
    public static JobStatusDto FromJob(DownloadJob job) => new(
        job.Id, job.VideoTitle, job.Format, job.Quality, job.Status, job.ProgressPercent,
        job.ErrorMessage, job.CreatedAt, job.CompletedAt, job.ExpiresAt);
}

// The caller owns and must dispose the returned stream.
public sealed record DownloadFileDto(Stream Stream, string FileName, string ContentType);

public sealed record MediaSourceDto(string? VideoPath, string AudioPath, TimeSpan Duration, string? ThumbnailPath = null)
{
    public IEnumerable<string> Paths => new[] { VideoPath, AudioPath, ThumbnailPath }.OfType<string>().Distinct();
}
