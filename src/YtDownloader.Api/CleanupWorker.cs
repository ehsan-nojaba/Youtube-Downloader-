using MediatR;
using YtDownloader.Application;

namespace YtDownloader.Api;

public sealed class CleanupWorker(IServiceScopeFactory scopes, ILogger<CleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<IMediator>()
                        .Send(new ExpireDownloadJobsCommand(), stoppingToken);
                    if (!result.IsSuccess) logger.LogWarning("Cleanup failed: {Message}", result.Error!.Message);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { logger.LogError(exception, "Cleanup failed; will retry on the next sweep"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
