using FluentValidation;
using YtDownloader.Domain;

namespace YtDownloader.Application;

public static class YoutubeUrl
{
    public static bool IsValid(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Host.ToLowerInvariant() is not ("youtube.com" or "www.youtube.com" or "youtu.be" or "music.youtube.com"))
            return false;

        var parameters = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Select(pair => (Key: Uri.UnescapeDataString(pair[0]), Value: pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : ""));
        if (parameters.Any(pair => pair.Key.Equals("list", StringComparison.OrdinalIgnoreCase)) ||
            uri.AbsolutePath.TrimEnd('/').Equals("/playlist", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
            return segments.Length == 1 && IsVideoId(segments[0]);
        if (uri.AbsolutePath == "/watch")
            return parameters.Count(pair => pair.Key == "v") == 1 &&
                parameters.Any(pair => pair.Key == "v" && IsVideoId(pair.Value));
        return segments.Length == 2 && segments[0] is "shorts" or "embed" or "live" && IsVideoId(segments[1]);
    }

    private static bool IsVideoId(string value) => value.Length == 11 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}

public sealed class GetVideoInfoQueryValidator : AbstractValidator<GetVideoInfoQuery>
{
    public GetVideoInfoQueryValidator() =>
        RuleFor(query => query.Url).Must(YoutubeUrl.IsValid).WithMessage("Provide a YouTube video URL without a playlist.");
}

public sealed class CreateDownloadJobCommandValidator : AbstractValidator<CreateDownloadJobCommand>
{
    public CreateDownloadJobCommandValidator()
    {
        RuleFor(command => command.Url).Must(YoutubeUrl.IsValid).WithMessage("Provide a YouTube video URL without a playlist.");
        RuleFor(command => command.Format).IsInEnum();
        RuleFor(command => command.Quality).Must((command, quality) => IsQualityValid(command.Format, quality))
            .WithMessage("Use a supported MP4 resolution or MP3 bitrate.");
    }

    private static bool IsQualityValid(MediaFormat format, string? quality) => format switch
    {
        MediaFormat.Mp4 => quality is "144p" or "240p" or "360p" or "480p" or "720p" or "1080p" or "1440p" or "2160p" or "4320p",
        MediaFormat.Mp3 => quality is "128k" or "192k" or "320k",
        _ => false
    };
}

public sealed class GetJobStatusQueryValidator : AbstractValidator<GetJobStatusQuery>
{
    public GetJobStatusQueryValidator() => RuleFor(query => query.JobId).NotEmpty();
}

public sealed class GetDownloadFileQueryValidator : AbstractValidator<GetDownloadFileQuery>
{
    public GetDownloadFileQueryValidator() => RuleFor(query => query.JobId).NotEmpty();
}

public sealed class ProcessDownloadJobCommandValidator : AbstractValidator<ProcessDownloadJobCommand>
{
    public ProcessDownloadJobCommandValidator() => RuleFor(command => command.JobId).NotEmpty();
}
