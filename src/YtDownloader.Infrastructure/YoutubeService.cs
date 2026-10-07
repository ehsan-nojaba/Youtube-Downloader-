using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using YoutubeExplode;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Videos.Streams;
using YtDownloader.Application;
using YtDownloader.Domain;

namespace YtDownloader.Infrastructure;

public sealed class YoutubeService(YoutubeClient youtube, IOptions<LocalFileStorageOptions> options,
    ILogger<YoutubeService>? logger = null, HttpClient? thumbnails = null) : IYoutubeService, IDisposable
{
    private readonly HttpClient thumbnailClient = thumbnails ?? new HttpClient();
    public void Dispose() => thumbnailClient.Dispose();
    public async Task<Result<VideoInfoDto>> GetVideoInfoAsync(string url, CancellationToken cancellationToken)
    {
        if (!YoutubeUrl.IsValid(url))
            return Result<VideoInfoDto>.Failure(new("Validation", "Provide a YouTube video URL without a playlist."));
        try
        {
            var video = await youtube.Videos.GetAsync(url, cancellationToken);
            if (video.Duration is not { } duration)
                return Result<VideoInfoDto>.Failure(new("UnsupportedVideo", "Live streams are not supported."));
            var manifest = await youtube.Videos.Streams.GetManifestAsync(url, cancellationToken);
            return Result<VideoInfoDto>.Success(new(
                video.Title, video.Author.ChannelTitle,
                video.Thumbnails.OrderByDescending(thumbnail => thumbnail.Resolution.Width).FirstOrDefault()?.Url ?? "",
                duration, MapVideoQualities(manifest), MapAudioQualities(manifest, duration)));
        }
        catch (Exception exception) when (IsServiceFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger?.LogWarning(exception, "YouTube metadata retrieval failed");
            return Result<VideoInfoDto>.Failure(new("YoutubeUnavailable", "YouTube metadata could not be retrieved."));
        }
    }

    public async Task<Result<MediaSourceDto>> DownloadAsync(string url, MediaFormat format, string quality,
        CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var validation = await new CreateDownloadJobCommandValidator()
            .ValidateAsync(new CreateDownloadJobCommand(url, format, quality), cancellationToken);
        if (!validation.IsValid)
            return Result<MediaSourceDto>.Failure(new("Validation", "Invalid URL, format, or quality."));

        var paths = new List<string>();
        var completed = false;
        try
        {
            var video = await youtube.Videos.GetAsync(url, cancellationToken);
            if (video.Duration is not { } duration)
                return Result<MediaSourceDto>.Failure(new("UnsupportedVideo", "Live streams are not supported."));
            var manifest = await youtube.Videos.Streams.GetManifestAsync(url, cancellationToken);
            var audio = manifest.GetAudioOnlyStreams().OrderByDescending(stream => stream.Bitrate.BitsPerSecond).FirstOrDefault();
            if (audio is null)
                return Result<MediaSourceDto>.Failure(new("StreamUnavailable", "No audio stream is available."));

            var videoStream = format == MediaFormat.Mp4
                ? VideoStreams(manifest).Where(stream => QualityLabel(stream) == quality)
                    .OrderBy(stream => stream is MuxedStreamInfo)
                    .ThenByDescending(stream => stream.Bitrate.BitsPerSecond).FirstOrDefault()
                : null;
            if (format == MediaFormat.Mp4 && videoStream is null)
                return Result<MediaSourceDto>.Failure(new("QualityUnavailable", "The selected video quality is unavailable."));

            Directory.CreateDirectory(System.IO.Path.GetFullPath(options.Value.TempPath));
            string? videoPath = null;
            var totalBytes = Math.Max(1, audio.Size.Bytes + (videoStream?.Size.Bytes ?? 0));
            if (videoStream is not null)
            {
                videoPath = NewTempPath(videoStream.Container.Name);
                paths.Add(videoPath);
                await youtube.Videos.Streams.DownloadAsync(videoStream, videoPath,
                    new InlineProgress(value => progress?.Report(value * videoStream.Size.Bytes / totalBytes)), cancellationToken);
            }
            var audioPath = NewTempPath(audio.Container.Name);
            paths.Add(audioPath);
            var downloadedBytes = videoStream?.Size.Bytes ?? 0;
            await youtube.Videos.Streams.DownloadAsync(audio, audioPath,
                new InlineProgress(value => progress?.Report((downloadedBytes + value * audio.Size.Bytes) / totalBytes)), cancellationToken);
            progress?.Report(1);
            string? thumbnailPath = null;
            if (format == MediaFormat.Mp3)
            {
                var thumbnail = video.Thumbnails.OrderByDescending(item => item.Resolution.Width).FirstOrDefault();
                if (thumbnail is null)
                    return Result<MediaSourceDto>.Failure(new("ThumbnailUnavailable", "The video has no thumbnail available for MP3 cover art."));
                thumbnailPath = NewTempPath("jpg");
                paths.Add(thumbnailPath);
                await using var image = await thumbnailClient.GetStreamAsync(thumbnail.Url, cancellationToken);
                await using var destination = new FileStream(thumbnailPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous);
                await image.CopyToAsync(destination, cancellationToken);
            }
            completed = true;
            return Result<MediaSourceDto>.Success(new(videoPath, audioPath, duration, thumbnailPath));
        }
        catch (Exception exception) when (IsServiceFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger?.LogWarning(exception, "YouTube source download failed");
            return Result<MediaSourceDto>.Failure(new("DownloadFailed", "The source media could not be downloaded."));
        }
        finally
        {
            if (!completed)
                foreach (var path in paths)
                    if (File.Exists(path)) File.Delete(path);
        }
    }

    public static QualityOptionDto[] MapVideoQualities(StreamManifest manifest)
    {
        var audio = manifest.GetAudioOnlyStreams().OrderByDescending(stream => stream.Bitrate.BitsPerSecond).FirstOrDefault();
        if (audio is null)
            return [];
        return VideoStreams(manifest).GroupBy(QualityLabel)
            .Select(group => group.OrderBy(stream => stream is MuxedStreamInfo)
                .ThenByDescending(stream => stream.Bitrate.BitsPerSecond).First())
            .OrderByDescending(stream => stream.VideoQuality.MaxHeight)
            .Select(stream => new QualityOptionDto(QualityLabel(stream), MediaFormat.Mp4,
                stream.Bitrate.BitsPerSecond, $"{stream.VideoResolution.Width}x{stream.VideoResolution.Height}",
                stream.Size.Bytes + audio.Size.Bytes)).ToArray();
    }

    public static QualityOptionDto[] MapAudioQualities(StreamManifest manifest, TimeSpan duration) =>
        !manifest.GetAudioOnlyStreams().Any() ? [] : new[] { 128, 192, 320 }
            .Select(bitrate => new QualityOptionDto($"{bitrate}k", MediaFormat.Mp3, bitrate * 1000L, null,
                (long)(duration.TotalSeconds * bitrate * 1000 / 8))).ToArray();

    private static IEnumerable<IVideoStreamInfo> VideoStreams(StreamManifest manifest) =>
        manifest.GetVideoOnlyStreams().Cast<IVideoStreamInfo>()
            .Concat(manifest.GetMuxedStreams()).Where(stream => stream.Container == Container.Mp4);

    private static string QualityLabel(IVideoStreamInfo stream) => $"{stream.VideoQuality.MaxHeight}p";
    private string NewTempPath(string extension) => System.IO.Path.Combine(
        System.IO.Path.GetFullPath(options.Value.TempPath), $"{Guid.NewGuid():N}.{extension}");

    private static bool IsServiceFailure(Exception exception) =>
        exception is YoutubeExplodeException or HttpRequestException or IOException or UnauthorizedAccessException;

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(Math.Clamp(value, 0, 1));
    }
}
