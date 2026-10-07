using System.ComponentModel;
using System.Globalization;
using CliWrap;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using YtDownloader.Application;
using YtDownloader.Domain;

namespace YtDownloader.Infrastructure;

public sealed class FfmpegConverter(IOptions<FfmpegOptions> options, ILogger<FfmpegConverter>? logger = null) : IMediaConverter
{
    public async Task<Result<bool>> ConvertAsync(MediaSourceDto source, string outputPath,
        MediaFormat format, string quality, Func<int, CancellationToken, Task> reportProgress,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(format) || (format == MediaFormat.Mp3 && quality is not ("128k" or "192k" or "320k")))
            return Result<bool>.Failure(new("Validation", "Unsupported output format or audio bitrate."));
        if ((format == MediaFormat.Mp4 && (source.VideoPath is null || !File.Exists(source.VideoPath))) ||
            !File.Exists(source.AudioPath) || (source.ThumbnailPath is not null && !File.Exists(source.ThumbnailPath)))
            return Result<bool>.Failure(new("FileNotFound", "A source media file is missing."));

        var arguments = BuildArguments(source, outputPath, format, quality);
        var lastPercent = -1;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath))!);
            var result = await Cli.Wrap(options.Value.Path)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .WithStandardErrorPipe(PipeTarget.ToStream(Stream.Null))
                .WithStandardOutputPipe(PipeTarget.ToDelegate(async (line, token) =>
                {
                    var percent = ParseProgress(line, source.Duration);
                    if (percent is { } value && value > lastPercent)
                    {
                        lastPercent = value;
                        await reportProgress(value, token);
                    }
                }))
                .ExecuteAsync(cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                File.Delete(outputPath);
                return Result<bool>.Failure(new("ConversionFailed", "FFmpeg could not convert the media."));
            }
            await reportProgress(100, cancellationToken);
            return Result<bool>.Success(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            File.Delete(outputPath);
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            logger?.LogError(exception, "FFmpeg conversion failed using executable {Executable}", options.Value.Path);
            File.Delete(outputPath);
            if (exception is Win32Exception { NativeErrorCode: 2 or 3 })
                return Result<bool>.Failure(new("ConversionFailed", "FFmpeg is not installed or its configured executable path is invalid."));
            return Result<bool>.Failure(new("ConversionFailed", "FFmpeg could not be started or the output could not be written."));
        }
    }

    public static string[] BuildArguments(MediaSourceDto source, string outputPath, MediaFormat format, string quality)
    {
        var arguments = new List<string> { "-nostdin", "-y", "-hide_banner", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };
        if (format == MediaFormat.Mp4)
            arguments.AddRange(["-i", source.VideoPath!, "-i", source.AudioPath, "-map", "0:v:0", "-map", "1:a:0",
                "-c:v", "copy", "-c:a", "aac", "-movflags", "+faststart", "-f", "mp4"]);
        else
        {
            arguments.AddRange(["-i", source.AudioPath]);
            if (source.ThumbnailPath is { } thumbnail)
                arguments.AddRange(["-i", thumbnail, "-map", "0:a:0", "-map", "1:v:0", "-c:v", "mjpeg",
                    "-disposition:v:0", "attached_pic", "-id3v2_version", "3",
                    "-metadata:s:v:0", "title=Album cover", "-metadata:s:v:0", "comment=Cover (front)"]);
            else
                arguments.AddRange(["-map", "0:a:0", "-vn"]);
            arguments.AddRange(["-c:a", "libmp3lame", "-b:a", quality, "-f", "mp3"]);
        }
        arguments.Add(outputPath);
        return arguments.ToArray();
    }

    public static int? ParseProgress(string line, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || !line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
            !long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
            return null;
        return (int)Math.Clamp(microseconds / 1_000_000d / duration.TotalSeconds * 100, 0, 99);
    }
}
