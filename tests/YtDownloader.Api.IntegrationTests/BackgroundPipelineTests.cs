using System.Net;
using System.Net.Http.Json;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using YtDownloader.Api;
using YtDownloader.Application;
using YtDownloader.Domain;

namespace YtDownloader.Api.IntegrationTests;

public sealed class BackgroundPipelineTests
{
    [Fact]
    public async Task AcceptedJobIsProcessedByHostedWorkerThroughRealMediator()
    {
        await using var factory = new ApiFactory(runWorker: true);
        var converter = Substitute.For<IMediaConverter>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Youtube.DownloadAsync(ApiFactory.VideoUrl, MediaFormat.Mp4, "1080p", Arg.Any<CancellationToken>())
            .Returns(Result<MediaSourceDto>.Success(new("video", "audio", TimeSpan.FromSeconds(10))));
        factory.Files.GetOutputPath(Arg.Any<Guid>(), MediaFormat.Mp4).Returns("output.mp4");
        converter.ConvertAsync(Arg.Any<MediaSourceDto>(), "output.mp4", MediaFormat.Mp4, "1080p",
            Arg.Any<Func<int, CancellationToken, Task>>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(call.Arg<CancellationToken>());
            return Result<bool>.Success(true);
        });
        var notifier = Substitute.For<IProgressNotifier>();
        notifier.NotifyAsync(Arg.Is<JobStatusDto>(job => job.Status == JobStatus.Completed), Arg.Any<CancellationToken>())
            .Returns(_ => { completed.TrySetResult(); return Task.CompletedTask; });
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMediaConverter>();
            services.AddSingleton(converter);
            services.RemoveAll<IProgressNotifier>();
            services.AddSingleton(notifier);
        }));
        using var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        try
        {
            var response = await client.PostAsJsonAsync("/api/downloads",
                new { url = ApiFactory.VideoUrl, format = "Mp4", quality = "1080p" });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var created = await response.Content.ReadFromJsonAsync<CreateDownloadResponse>();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var scope = host.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<YtDownloader.Infrastructure.AppDbContext>();
            var job = await scope.ServiceProvider.GetRequiredService<IJobRepository>().GetByIdAsync(created!.JobId, default);
            Assert.Equal(JobStatus.Processing, job!.Status);
            release.TrySetResult();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await context.Entry(job).ReloadAsync();
            Assert.Equal(JobStatus.Completed, job.Status);
            Assert.Equal("output.mp4", job.FilePath);
        }
        finally { release.TrySetResult(); }
    }
}
