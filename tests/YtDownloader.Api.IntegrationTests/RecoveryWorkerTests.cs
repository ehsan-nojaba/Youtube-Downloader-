using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using YtDownloader.Api;
using YtDownloader.Application;
using YtDownloader.Domain;
using YtDownloader.Infrastructure;

namespace YtDownloader.Api.IntegrationTests;

public sealed class RecoveryWorkerTests
{
    [Fact]
    public async Task StartupRecoveryDoesNotDeadlockWhenBacklogExceedsQueueCapacity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<DatabaseInitializer>();
        await using var provider = services.BuildServiceProvider();
        var ids = new List<Guid>();
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.Database.MigrateAsync();
            for (var index = 0; index < 105; index++)
            {
                var job = new DownloadJob(ApiFactory.VideoUrl, MediaFormat.Mp4, "1080p");
                if (index == 0) { job.Start(); job.ReportProgress(80); }
                ids.Add(job.Id);
                context.Add(job);
            }
            await context.SaveChangesAsync();
        }
        var queue = new InMemoryJobQueue(Options.Create(new JobQueueOptions { Capacity = 1 }));
        using var worker = new JobRecoveryWorker(provider.GetRequiredService<IServiceScopeFactory>(), queue);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(timeout.Token);
        try
        {
            var restored = new List<Guid>();
            for (var index = 0; index < ids.Count; index++) restored.Add(await queue.DequeueAsync(timeout.Token));
            Assert.Equal(ids.Order(), restored.Order());
            await worker.ExecuteTask!.WaitAsync(timeout.Token);
            await using var scope = provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(105, await context.DownloadJobs.CountAsync(job => job.Status == JobStatus.Queued));
            Assert.All(await context.DownloadJobs.ToListAsync(), job => Assert.Equal(0, job.ProgressPercent));
        }
        finally { await worker.StopAsync(timeout.Token); }
    }
}
