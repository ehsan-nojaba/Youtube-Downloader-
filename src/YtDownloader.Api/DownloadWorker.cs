using System.Collections.Concurrent;
using MediatR;
using Microsoft.Extensions.Options;
using YtDownloader.Application;
using YtDownloader.Domain;

namespace YtDownloader.Api;

public sealed class DownloadWorker(
    IJobQueue queue, IServiceScopeFactory scopes, IOptions<DownloadWorkerOptions> options,
    ILogger<DownloadWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, byte> activeJobs = new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, options.Value.MaxParallelJobs).Select(_ => ConsumeAsync(stoppingToken)));

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid jobId;
            try { jobId = await queue.DequeueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not dequeue a download job");
                try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                continue;
            }
            if (!activeJobs.TryAdd(jobId, 0)) continue;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                try
                {
                    var result = await scope.ServiceProvider.GetRequiredService<IMediator>()
                        .Send(new ProcessDownloadJobCommand(jobId), stoppingToken);
                    if (!result.IsSuccess)
                        await MarkFailedAsync(scope.ServiceProvider, jobId, result.Error!.Message);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    await MarkFailedAsync(scope.ServiceProvider, jobId, "Download cancelled because the service is stopping.");
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Download job {JobId} failed", jobId);
                    await MarkFailedAsync(scope.ServiceProvider, jobId, "An unexpected error occurred while processing the download.");
                }
            }
            finally { activeJobs.TryRemove(jobId, out _); }
        }
    }

    private async Task MarkFailedAsync(IServiceProvider services, Guid jobId, string message)
    {
        try
        {
            // Persist failure even when the worker's shutdown token has been cancelled.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var job = await services.GetRequiredService<IJobRepository>().GetByIdAsync(jobId, timeout.Token);
            if (job is null || job.Status is not (JobStatus.Queued or JobStatus.Processing)) return;
            job.Fail(message);
            await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(timeout.Token);
            await services.GetRequiredService<IProgressNotifier>().NotifyAsync(JobStatusDto.FromJob(job), timeout.Token);
        }
        catch (Exception exception) { logger.LogError(exception, "Could not persist or notify failure for job {JobId}", jobId); }
    }
}
