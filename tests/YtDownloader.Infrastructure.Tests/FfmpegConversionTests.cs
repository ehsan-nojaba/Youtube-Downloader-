using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Options;
using YtDownloader.Application;
using YtDownloader.Domain;
using YtDownloader.Infrastructure;

namespace YtDownloader.Infrastructure.Tests;

public sealed class FfmpegTheoryAttribute : TheoryAttribute
{
    public FfmpegTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YTDOWNLOADER_FFMPEG_TEST_PATH")))
            Skip = "Set YTDOWNLOADER_FFMPEG_TEST_PATH to run real FFmpeg conversion checks.";
    }
}

public sealed class FfmpegConversionTests
{
    [FfmpegTheory]
    [Trait("Category", "FFmpeg")]
    [InlineData(MediaFormat.Mp3, false)]
    [InlineData(MediaFormat.Mp3, true)]
    [InlineData(MediaFormat.Mp4, false)]
    public async Task ConvertsSyntheticMediaWithRealFfmpeg(MediaFormat format, bool withCover)
    {
        var executable = Environment.GetEnvironmentVariable("YTDOWNLOADER_FFMPEG_TEST_PATH")!;
        var directory = Path.Combine(Path.GetTempPath(), $"YtDownloader-ffmpeg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var audio = Path.Combine(directory, "audio.webm");
        var video = Path.Combine(directory, "video.mp4");
        var cover = Path.Combine(directory, "cover.jpg");
        var output = Path.Combine(directory, format == MediaFormat.Mp3 ? "output.mp3" : "output.mp4");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await Cli.Wrap(executable).WithArguments(["-nostdin", "-y", "-f", "lavfi", "-i", "sine=frequency=440",
                "-t", "1", "-c:a", "libopus", audio]).ExecuteBufferedAsync(timeout.Token);
            if (format == MediaFormat.Mp4)
                await Cli.Wrap(executable).WithArguments(["-nostdin", "-y", "-f", "lavfi", "-i", "color=size=64x64:rate=10",
                    "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-an", video]).ExecuteBufferedAsync(timeout.Token);
            if (withCover)
                await Cli.Wrap(executable).WithArguments(["-nostdin", "-y", "-f", "lavfi", "-i", "color=c=teal:size=64x64",
                    "-frames:v", "1", "-update", "1", cover]).ExecuteBufferedAsync(timeout.Token);
            var progress = new List<int>();
            var result = await new FfmpegConverter(Options.Create(new FfmpegOptions { Path = executable }))
                .ConvertAsync(new(format == MediaFormat.Mp4 ? video : null, audio, TimeSpan.FromSeconds(1), withCover ? cover : null), output,
                    format, format == MediaFormat.Mp3 ? "192k" : "1080p",
                    (percent, _) => { progress.Add(percent); return Task.CompletedTask; }, timeout.Token);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.True(new FileInfo(output).Length > 0);
            Assert.Equal(100, progress[^1]);
            Assert.Equal(progress.Order(), progress);
            var probe = await Cli.Wrap(Path.Combine(Path.GetDirectoryName(executable)!, "ffprobe.exe"))
                .WithArguments(["-v", "error", "-show_streams", "-of", "json", output]).ExecuteBufferedAsync(timeout.Token);
            using var document = JsonDocument.Parse(probe.StandardOutput);
            var codecs = document.RootElement.GetProperty("streams").EnumerateArray()
                .Select(stream => stream.GetProperty("codec_name").GetString()).ToArray();
            Assert.Contains(format == MediaFormat.Mp3 ? "mp3" : "aac", codecs);
            if (format == MediaFormat.Mp4) Assert.Contains("h264", codecs);
            else if (withCover)
            {
                Assert.Contains("mjpeg", codecs);
                var artwork = document.RootElement.GetProperty("streams").EnumerateArray()
                    .Single(stream => stream.GetProperty("codec_name").GetString() == "mjpeg");
                Assert.Equal(1, artwork.GetProperty("disposition").GetProperty("attached_pic").GetInt32());
                Assert.Equal("Album cover", artwork.GetProperty("tags").GetProperty("title").GetString());
            }
            else Assert.Single(codecs);
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
