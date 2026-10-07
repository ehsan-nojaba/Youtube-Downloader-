using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using YoutubeExplode.Common;
using YoutubeExplode.Videos.Streams;
using YtDownloader.Application;
using YtDownloader.Domain;
using YtDownloader.Infrastructure;

namespace YtDownloader.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public async Task BoundedQueueWaitsForSpaceAndHonorsCancellation()
    {
        var queue = new InMemoryJobQueue(Options.Create(new JobQueueOptions { Capacity = 1 }));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await queue.EnqueueAsync(first, default);
        var blocked = queue.EnqueueAsync(second, default);
        Assert.False(blocked.IsCompleted);
        Assert.Equal(first, await queue.DequeueAsync(default));
        await blocked.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var cancelled = queue.EnqueueAsync(Guid.NewGuid(), cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(second, await queue.DequeueAsync(default));
    }
    [Theory]
    [InlineData("out_time_us=5000000", 50)]
    [InlineData("out_time_us=20000000", 99)]
    [InlineData("out_time_us=-1000000", 0)]
    [InlineData("out_time_us=N/A", null)]
    [InlineData("progress=end", null)]
    public void ParsesFfmpegProgress(string line, int? expected) =>
        Assert.Equal(expected, FfmpegConverter.ParseProgress(line, TimeSpan.FromSeconds(10)));

    [Fact]
    public void UnknownDurationDoesNotInventPercentage() =>
        Assert.Null(FfmpegConverter.ParseProgress("out_time_us=1000", TimeSpan.Zero));

    [Theory]
    [InlineData(MediaFormat.Mp4, "1080p")]
    [InlineData(MediaFormat.Mp3, "192k")]
    public void FfmpegArgumentsUseExplicitMappingAndRequestedCodecs(MediaFormat format, string quality)
    {
        var args = FfmpegConverter.BuildArguments(new("video with spaces.mp4", "audio.webm", TimeSpan.FromSeconds(10)),
            "output with spaces", format, quality);
        Assert.Equal("output with spaces", args[^1]);
        Assert.Contains("-nostdin", args);
        Assert.Contains("pipe:1", args);
        if (format == MediaFormat.Mp4)
        {
            Assert.Contains("video with spaces.mp4", args);
            Assert.Contains("0:v:0", args);
            Assert.Contains("1:a:0", args);
            Assert.Equal("copy", args[Array.IndexOf(args, "-c:v") + 1]);
            Assert.Equal("aac", args[Array.IndexOf(args, "-c:a") + 1]);
        }
        else
        {
            Assert.DoesNotContain("video with spaces.mp4", args);
            Assert.Contains("-vn", args);
            Assert.Equal(quality, args[Array.IndexOf(args, "-b:a") + 1]);
        }
    }

    [Fact]
    public void MapsMuxedVideoOnlyAndAudioStreamsToOutputChoices()
    {
        var manifest = new StreamManifest([
            new AudioOnlyStreamInfo("https://example.com/a", Container.WebM, new FileSize(200), new Bitrate(160000), "opus", null, true),
            new VideoOnlyStreamInfo("https://example.com/v", Container.Mp4, new FileSize(1000), new Bitrate(1000000),
                "avc1", new VideoQuality(1080, 60), new Resolution(1920, 1080)),
            new MuxedStreamInfo("https://example.com/m", Container.Mp4, new FileSize(500), new Bitrate(500000),
                "aac", null, true, "avc1", new VideoQuality(360, 30), new Resolution(640, 360))
        ]);
        var video = YoutubeService.MapVideoQualities(manifest);
        Assert.Equal(["1080p", "360p"], video.Select(option => option.Label));
        Assert.Equal(1200, video[0].EstimatedSizeBytes);
        Assert.Equal("1920x1080", video[0].Resolution);
        var audio = YoutubeService.MapAudioQualities(manifest, TimeSpan.FromSeconds(10));
        Assert.Equal(["128k", "192k", "320k"], audio.Select(option => option.Label));
        Assert.Equal(240000, audio[1].EstimatedSizeBytes);
        Assert.Equal(192000, audio[1].Bitrate);
    }

    [Fact]
    public async Task StorageSanitizesSavesReadsChecksAndDeletes()
    {
        var options = TestOptions();
        var storage = new LocalFileStorage(Options.Create(options));
        using var source = new MemoryStream([1, 2, 3]);
        var saved = await storage.SaveAsync("../bad: name?.mp3", source, default);
        Assert.True(saved.IsSuccess);
        var path = saved.Value!;
        try
        {
            Assert.Equal(Path.GetFullPath(options.RootPath), Path.GetDirectoryName(path));
            Assert.DoesNotContain('?', Path.GetFileName(path));
            Assert.True(await storage.ExistsAsync(path, default));
            var result = await storage.OpenReadAsync(path, default);
            Assert.True(result.IsSuccess);
            await using var stream = result.Value!.Stream;
            Assert.Equal("audio/mpeg", result.Value.ContentType);
            using var contents = new MemoryStream();
            await stream.CopyToAsync(contents);
            Assert.Equal(new byte[] { 1, 2, 3 }, contents.ToArray());
        }
        finally
        {
            await storage.DeleteAsync(path, default);
            Directory.Delete(options.RootPath);
        }
        Assert.False(await storage.ExistsAsync(path, default));
    }

    [Fact]
    public async Task StorageRejectsOutsidePathsAndDeletesTemporaryMedia()
    {
        var options = TestOptions();
        var storage = new LocalFileStorage(Options.Create(options));
        var outside = Path.Combine(Path.GetTempPath(), "outside.mp3");
        Assert.Equal("InvalidPath", (await storage.OpenReadAsync(outside, default)).Error!.Code);
        Assert.False(await storage.ExistsAsync(outside, default));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.DeleteAsync(outside, default));
        Directory.CreateDirectory(options.TempPath);
        var temp = Path.Combine(options.TempPath, "source.webm");
        await File.WriteAllTextAsync(temp, "source");
        await storage.DeleteAsync(temp, default);
        Assert.False(File.Exists(temp));
        Directory.Delete(options.TempPath);
    }

    [Fact]
    public async Task StorageHonorsCancellationAndRemovesPartialSave()
    {
        var options = TestOptions();
        var storage = new LocalFileStorage(Options.Create(options));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var source = new MemoryStream([1]);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.SaveAsync("file.mp3", source, cancellation.Token));
            Assert.Empty(Directory.EnumerateFiles(options.RootPath));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.ExistsAsync("missing", cancellation.Token));
        }
        finally
        {
            if (Directory.Exists(options.RootPath)) Directory.Delete(options.RootPath);
        }
    }

    [Fact]
    public async Task MissingFfmpegReturnsFailureAndRemovesOutput()
    {
        var options = TestOptions();
        Directory.CreateDirectory(options.TempPath);
        var source = Path.Combine(options.TempPath, "audio.webm");
        var output = Path.Combine(options.TempPath, "output.mp3");
        await File.WriteAllTextAsync(source, "source");
        try
        {
            var converter = new FfmpegConverter(Options.Create(new FfmpegOptions { Path = Path.Combine(options.TempPath, "missing-ffmpeg") }));
            var result = await converter.ConvertAsync(new(null, source, TimeSpan.FromSeconds(1)),
                output, MediaFormat.Mp3, "192k", (_, _) => Task.CompletedTask, default);
            Assert.Equal("ConversionFailed", result.Error!.Code);
            Assert.False(File.Exists(output));
        }
        finally
        {
            File.Delete(source);
            Directory.Delete(options.TempPath);
        }
    }

    [Fact]
    public async Task AddInfrastructureBindsOptionsAndSharesRepositoryWithUnitOfWork()
    {
        var options = TestOptions();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ffmpeg:Path"] = "custom-ffmpeg",
            ["ConnectionStrings:Jobs"] = "Data Source=:memory:",
            ["FileStorage:RootPath"] = options.RootPath,
            ["FileStorage:TempPath"] = options.TempPath
        }).Build();
        await using var provider = new ServiceCollection().AddInfrastructure(configuration).BuildServiceProvider();
        Assert.Equal("custom-ffmpeg", provider.GetRequiredService<IOptions<FfmpegOptions>>().Value.Path);
        Assert.Equal(options.RootPath, provider.GetRequiredService<IOptions<LocalFileStorageOptions>>().Value.RootPath);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.OpenConnectionAsync();
        await context.Database.MigrateAsync();
        var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        Assert.Same(repository, unitOfWork);
        var job = new DownloadJob("https://youtu.be/dQw4w9WgXcQ", MediaFormat.Mp3, "128k");
        await repository.AddAsync(job, default);
        await unitOfWork.SaveChangesAsync(default);
        Assert.Same(job, await repository.GetByIdAsync(job.Id, default));
        Assert.IsType<YoutubeService>(provider.GetRequiredService<IYoutubeService>());
        Assert.IsType<FfmpegConverter>(provider.GetRequiredService<IMediaConverter>());
        Assert.IsType<LocalFileStorage>(provider.GetRequiredService<IFileStorage>());
    }

    private static LocalFileStorageOptions TestOptions()
    {
        var id = Guid.NewGuid().ToString("N");
        return new()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"YtDownloader-storage-{id}"),
            TempPath = Path.Combine(Path.GetTempPath(), $"YtDownloader-source-{id}")
        };
    }
}
