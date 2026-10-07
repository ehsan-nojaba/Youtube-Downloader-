using Microsoft.Extensions.Options;
using YoutubeExplode;
using YtDownloader.Domain;
using YtDownloader.Infrastructure;

namespace YtDownloader.Infrastructure.Tests;

public sealed class YoutubeIntegrationFactAttribute : FactAttribute
{
    public YoutubeIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("YTDOWNLOADER_RUN_INTEGRATION") != "1")
            Skip = "Set YTDOWNLOADER_RUN_INTEGRATION=1 to enable live YouTube downloads.";
    }
}

public sealed class YoutubeIntegrationTests
{
    [YoutubeIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task DownloadsShortCreativeCommonsVideo()
    {
        // Big Buck Bunny loves Creative Commons, Renderfarm.fi & Studio Lumikuu, CC BY 3.0.
        // Source and license: https://resources.creativecommons.org/big-buck-bunny-loves-creative-commons/
        const string url = "https://www.youtube.com/watch?v=4-Ddumty4mk";
        var temp = Path.Combine(Path.GetTempPath(), $"YtDownloader-integration-{Guid.NewGuid():N}");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var youtube = new YoutubeClient();
        var service = new YoutubeService(youtube, Options.Create(new LocalFileStorageOptions { TempPath = temp }));
        var progress = new RecordedProgress();
        try
        {
            var info = await service.GetVideoInfoAsync(url, cancellation.Token);
            Assert.True(info.IsSuccess, info.Error?.Message);
            Assert.InRange(info.Value!.Duration, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(3));
            var result = await service.DownloadAsync(url, MediaFormat.Mp3, "128k", cancellation.Token, progress);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.True(new FileInfo(result.Value!.AudioPath).Length > 0);
            Assert.Null(result.Value.VideoPath);
            Assert.Equal(1, progress.LastValue);
        }
        finally
        {
            // This test owns this unique directory; delete files without recursive removal.
            if (Directory.Exists(temp))
            {
                foreach (var path in Directory.EnumerateFiles(temp)) File.Delete(path);
                Directory.Delete(temp);
            }
        }
    }

    private sealed class RecordedProgress : IProgress<double>
    {
        public double LastValue { get; private set; }
        public void Report(double value) => LastValue = value;
    }
}
