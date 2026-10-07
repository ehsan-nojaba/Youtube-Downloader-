using FluentValidation.TestHelper;
using YtDownloader.Application;
using YtDownloader.Domain;

namespace YtDownloader.Application.UnitTests;

public sealed class ValidatorTests
{
    [Theory]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/live/dQw4w9WgXcQ")]
    public async Task AcceptsYouTubeVideoUrls(string url)
    {
        var result = await new GetVideoInfoQueryValidator().TestValidateAsync(new GetVideoInfoQuery(url));
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a url")]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com.evil.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://evilyoutube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com@evil.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://user@youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("ftp://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com:444/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/playlist?list=PL123")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&list=PL123")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?%6cist=PL123")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&LIST=")]
    [InlineData("https://youtube.com/")]
    [InlineData("https://youtube.com/watch?v=short")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&v=dQw4w9WgXcQ")]
    public async Task RejectsInvalidUrlsAndPlaylists(string? url)
    {
        (await new GetVideoInfoQueryValidator().TestValidateAsync(new GetVideoInfoQuery(url!)))
            .ShouldHaveValidationErrorFor(query => query.Url);
        (await new CreateDownloadJobCommandValidator().TestValidateAsync(new CreateDownloadJobCommand(url!, MediaFormat.Mp4, "1080p")))
            .ShouldHaveValidationErrorFor(command => command.Url);
    }

    [Theory]
    [InlineData(MediaFormat.Mp4, "144p")]
    [InlineData(MediaFormat.Mp4, "1080p")]
    [InlineData(MediaFormat.Mp4, "2160p")]
    [InlineData(MediaFormat.Mp3, "128k")]
    [InlineData(MediaFormat.Mp3, "192k")]
    [InlineData(MediaFormat.Mp3, "320k")]
    public async Task AcceptsValidQualityCombinations(MediaFormat format, string quality)
    {
        var result = await new CreateDownloadJobCommandValidator().TestValidateAsync(new CreateDownloadJobCommand(HandlerTests.Url, format, quality));
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(MediaFormat.Mp4, "192k")]
    [InlineData(MediaFormat.Mp3, "1080p")]
    [InlineData(MediaFormat.Mp4, "999p")]
    [InlineData(MediaFormat.Mp3, "999k")]
    [InlineData(MediaFormat.Mp3, "")]
    [InlineData(MediaFormat.Mp4, null)]
    [InlineData((MediaFormat)999, "1080p")]
    public async Task RejectsInvalidQualityCombinations(MediaFormat format, string? quality)
    {
        var result = await new CreateDownloadJobCommandValidator().TestValidateAsync(new CreateDownloadJobCommand(HandlerTests.Url, format, quality!));
        result.ShouldHaveValidationErrorFor(command => command.Quality);
        if (!Enum.IsDefined(format))
            result.ShouldHaveValidationErrorFor(command => command.Format);
    }

    [Fact]
    public async Task RejectsEmptyJobIds()
    {
        Assert.False((await new GetJobStatusQueryValidator().ValidateAsync(new GetJobStatusQuery(Guid.Empty))).IsValid);
        Assert.False((await new GetDownloadFileQueryValidator().ValidateAsync(new GetDownloadFileQuery(Guid.Empty))).IsValid);
        Assert.False((await new ProcessDownloadJobCommandValidator().ValidateAsync(new ProcessDownloadJobCommand(Guid.Empty))).IsValid);
    }
}


