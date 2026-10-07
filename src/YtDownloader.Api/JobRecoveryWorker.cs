using YtDownloader.Application;
using YtDownloader.Infrastructure;

namespace YtDownloader.Api;

public sealed class JobRecoveryWorker(IServiceScopeFactory scopes, IJobQueue queue) : BackgroundService
{
    private IReadOnlyList<Guid> pending = [];
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        pending = await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Workers can drain the queue while a backlog larger than its capacity is restored.
            foreach (var id in pending) await queue.EnqueueAsync(id, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
