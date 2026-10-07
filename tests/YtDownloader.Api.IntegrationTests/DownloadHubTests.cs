using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using YtDownloader.Api;
using YtDownloader.Application;
using YtDownloader.Domain;

namespace YtDownloader.Api.IntegrationTests;

public sealed class DownloadHubTests
{
    [Fact]
    public async Task JoiningReturnsSnapshotAndProgressIsSentOnlyToJobGroup()
    {
        await using var factory = new ApiFactory();
        using var client = factory.NewClient();
        var first = new DownloadJob(ApiFactory.VideoUrl, MediaFormat.Mp4, "1080p");
        var second = new DownloadJob(ApiFactory.VideoUrl, MediaFormat.Mp4, "1080p");
        await factory.SeedAsync(first, second);
        await using var connection = Connection(factory);
        var firstSnapshot = new TaskCompletionSource<DownloadProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = new TaskCompletionSource<DownloadProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<DownloadProgress>("JobProgress", progress =>
        {
            if (progress.Status == "Queued") firstSnapshot.TrySetResult(progress);
            if (progress.Status == "Processing") update.TrySetResult(progress);
        });
        await connection.StartAsync();
        Assert.True(await connection.InvokeAsync<bool>("JoinJob", first.Id));
        Assert.Equal(first.Id, (await firstSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(10))).JobId);
        first.Start();
        first.ReportProgress(42);
        await factory.Services.GetRequiredService<IProgressNotifier>().NotifyAsync(JobStatusDto.FromJob(first), default);
        var progress = await update.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(first.Id, progress.JobId);
        Assert.Equal(42, progress.ProgressPercent);
        Assert.False(await connection.InvokeAsync<bool>("JoinJob", Guid.NewGuid()));
        Assert.False(await connection.InvokeAsync<bool>("JoinJob", Guid.Empty));

        // A second connection joins only the other job and receives no updates for the first job.
        await using var otherConnection = Connection(factory);
        var otherSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrongGroupReceived = false;
        otherConnection.On<DownloadProgress>("JobProgress", item =>
        {
            if (item.JobId == second.Id) otherSnapshot.TrySetResult();
            if (item.JobId == first.Id) wrongGroupReceived = true;
        });
        await otherConnection.StartAsync();
        Assert.True(await otherConnection.InvokeAsync<bool>("JoinJob", second.Id));
        await otherSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(10));
        first.ReportProgress(70);
        await factory.Services.GetRequiredService<IProgressNotifier>().NotifyAsync(JobStatusDto.FromJob(first), default);
        await otherConnection.InvokeAsync<bool>("JoinJob", second.Id);
        Assert.False(wrongGroupReceived);
    }

    private static HubConnection Connection(ApiFactory factory) =>
        new HubConnectionBuilder().WithUrl("https://localhost/hubs/downloads", options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
        }).Build();
}
