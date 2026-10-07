using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using YtDownloader.Application;
using YtDownloader.Domain;

namespace YtDownloader.Api.IntegrationTests;

public sealed class ApiTests
{
    [Fact]
    public async Task GetsVideoInfoThroughRealMediator()
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        var response = await client.GetAsync(InfoUrl(ApiFactory.VideoUrl));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Test video", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("Mp4", body.RootElement.GetProperty("videoQualities")[0].GetProperty("format").GetString());
        await factory.Youtube.Received(1).GetVideoInfoAsync(ApiFactory.VideoUrl, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Mp4", "1080p")]
    [InlineData("Mp3", "192k")]
    public async Task CreatesJobAndReturnsLocationStatusAndQueuedId(string format, string quality)
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        var response = await client.PostAsJsonAsync("/api/downloads", new { url = ApiFactory.VideoUrl, format, quality });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, Guid>>();
        var id = body!["jobId"];
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal($"/api/downloads/{id}", response.Headers.Location!.OriginalString);
        var status = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var snapshot = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.Equal("Queued", snapshot.RootElement.GetProperty("status").GetString());
        Assert.Equal(format, snapshot.RootElement.GetProperty("format").GetString());
        Assert.Equal("Test video", snapshot.RootElement.GetProperty("videoTitle").GetString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.Equal(id, await factory.Services.GetRequiredService<IJobQueue>().DequeueAsync(timeout.Token));
    }

    [Theory]
    [InlineData(MediaFormat.Mp4, "video/mp4", ".mp4")]
    [InlineData(MediaFormat.Mp3, "audio/mpeg", ".mp3")]
    public async Task StreamsFileWithCorrectHeadersAndDisposesStream(MediaFormat format, string contentType, string extension)
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        var job = new DownloadJob(ApiFactory.VideoUrl, format, format == MediaFormat.Mp4 ? "1080p" : "192k");
        job.Start();
        job.Complete("output", DateTimeOffset.UtcNow.AddHours(1));
        await factory.SeedAsync(job);
        var stream = new MemoryStream([1, 2, 3, 4]);
        factory.Files.OpenReadAsync("output", Arg.Any<CancellationToken>())
            .Returns(Result<DownloadFileDto>.Success(new(stream, "output", "unknown")));
        var response = await client.GetAsync($"/api/downloads/{job.Id}/file");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType!.MediaType);
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Equal(job.Id + extension, disposition.FileNameStar);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
        Assert.False(stream.CanRead);
    }

    [Theory]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&list=PL123")]
    [InlineData("not-a-url")]
    public async Task InvalidInfoUrlReturnsValidationProblemWithoutCallingYoutube(string url)
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        var response = await client.GetAsync(InfoUrl(url));
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Validation", "Url");
        await factory.Youtube.DidNotReceiveWithAnyArgs().GetVideoInfoAsync(default!, default);
    }

    [Theory]
    [InlineData("https://evil.com", "Mp4", "1080p", "Url")]
    [InlineData(ApiFactory.VideoUrl, "Mp4", "192k", "Quality")]
    [InlineData(ApiFactory.VideoUrl, "Mp3", "1080p", "Quality")]
    [InlineData(ApiFactory.VideoUrl, "Mp4", "", "Quality")]
    public async Task InvalidCommandReturnsFieldErrorsWithoutCreatingJob(string url, string format, string quality, string field)
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        var response = await client.PostAsJsonAsync("/api/downloads", new { url, format, quality });
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Validation", field);
        await factory.Youtube.DidNotReceiveWithAnyArgs().GetVideoInfoAsync(default!, default);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"url\":\"https://youtu.be/dQw4w9WgXcQ\",\"format\":\"Avi\",\"quality\":\"1080p\"}")]
    [InlineData("{\"url\":\"https://youtu.be/dQw4w9WgXcQ\",\"format\":9,\"quality\":\"1080p\"}")]
    public async Task InvalidJsonAndFormatsReturnProblemDetails(string json)
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        var response = await client.PostAsync("/api/downloads", new StringContent(json, Encoding.UTF8, "application/json"));
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.DoesNotContain("JsonException", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MissingQueryParameterReturnsProblemDetails()
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        await AssertProblemAsync(await client.GetAsync("/api/videos/info"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EmptyJobIdReturnsValidationProblem()
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        await AssertProblemAsync(await client.GetAsync($"/api/downloads/{Guid.Empty}"), HttpStatusCode.BadRequest, "Validation", "JobId");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownJobReturnsNotFound(bool file)
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        await AssertProblemAsync(await client.GetAsync($"/api/downloads/{Guid.NewGuid()}{(file ? "/file" : "")}"),
            HttpStatusCode.NotFound, "NotFound");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingOrExpiredFileReturnsProblem(bool expired)
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        var job = new DownloadJob(ApiFactory.VideoUrl, MediaFormat.Mp4, "1080p");
        if (expired)
        {
            job.Fail("Failed");
            job.Expire();
        }
        await factory.SeedAsync(job);
        await AssertProblemAsync(await client.GetAsync($"/api/downloads/{job.Id}/file"),
            expired ? HttpStatusCode.Gone : HttpStatusCode.Conflict, expired ? "Expired" : "NotReady");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsVideosLongerThanDefaultDuration(bool create)
    {
        await using var factory = new ApiFactory();
        factory.Youtube.GetVideoInfoAsync(ApiFactory.VideoUrl, Arg.Any<CancellationToken>())
            .Returns(Result<VideoInfoDto>.Success(ApiFactory.Info with { Duration = TimeSpan.FromMinutes(121) }));
        using var client = factory.NewClient();
        var response = create
            ? await client.PostAsJsonAsync("/api/downloads", new { url = ApiFactory.VideoUrl, format = "Mp4", quality = "1080p" })
            : await client.GetAsync(InfoUrl(ApiFactory.VideoUrl));
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "VideoTooLong");
    }

    [Fact]
    public async Task UnexpectedExceptionReturnsSafeProblemDetails()
    {
        await using var factory = new ApiFactory();
        factory.Youtube.GetVideoInfoAsync(ApiFactory.VideoUrl, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result<VideoInfoDto>>(new InvalidOperationException("secret internal path")));
        using var client = factory.NewClient();
        var response = await client.GetAsync(InfoUrl(ApiFactory.VideoUrl));
        await AssertProblemAsync(response, HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain("stackTrace", body);
        Assert.Contains("traceId", body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsOversizedKnownAndChunkedBodies(bool chunked)
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        using HttpContent content = chunked
            ? new UnknownLengthContent(new string('x', 17000))
            : new StringContent(new string('x', 17000), Encoding.UTF8, "application/json");
        await AssertProblemAsync(await client.PostAsync("/api/downloads", content), HttpStatusCode.RequestEntityTooLarge);
        await factory.Youtube.DidNotReceiveWithAnyArgs().GetVideoInfoAsync(default!, default);
    }

    [Fact]
    public async Task FixedWindowRejectsAfterThirtyRequestsAndIgnoresSpoofedForwardedIp()
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        for (var index = 0; index < 30; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, InfoUrl("invalid"));
            request.Headers.Add("X-Forwarded-For", $"10.0.0.{index}");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        }
        var response = await client.GetAsync(InfoUrl("invalid"));
        await AssertProblemAsync(response, HttpStatusCode.TooManyRequests);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Theory]
    [InlineData("http://localhost:5147", true)]
    [InlineData("https://evil.com", false)]
    public async Task CorsAllowsOnlyConfiguredBlazorOrigins(string origin, bool allowed)
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/downloads");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        var response = await client.SendAsync(request);
        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
        if (allowed) Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task HealthOpenApiAndScalarAreAvailable()
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var openApi = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        using var document = JsonDocument.Parse(await openApi.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/videos/info", out _));
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/downloads/{id}/file", out _));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/scalar/v1")).StatusCode);
    }

    private static string InfoUrl(string url) => "/api/videos/info?url=" + Uri.EscapeDataString(url);

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? code = null, string? field = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)status, body.RootElement.GetProperty("status").GetInt32());
        if (code is not null) Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        if (field is not null) Assert.True(body.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    private sealed class UnknownLengthContent(string text) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();
    }
}

