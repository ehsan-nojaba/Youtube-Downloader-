using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using YtDownloader.Domain;

namespace YtDownloader.Api.IntegrationTests;

public sealed class HistoryTests
{
    [Fact]
    public async Task HistoryListsOnlyRequestedBrowserJobIds()
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        var own = new DownloadJob(ApiFactory.VideoUrl, MediaFormat.Mp4, "1080p", "Own video");
        var other = new DownloadJob(ApiFactory.VideoUrl, MediaFormat.Mp3, "192k", "Other browser");
        await factory.SeedAsync(own, other);
        var response = await client.PostAsJsonAsync("/api/downloads/history", new { jobIds = new[] { own.Id } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetArrayLength());
        Assert.Equal(own.Id, body.RootElement[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task InvalidHistoryIdsReturnValidationProblem()
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        var response = await client.PostAsJsonAsync("/api/downloads/history", new { jobIds = new[] { Guid.Empty } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }
}
