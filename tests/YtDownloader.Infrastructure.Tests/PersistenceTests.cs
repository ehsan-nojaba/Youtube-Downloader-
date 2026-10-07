using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using YtDownloader.Application;
using YtDownloader.Domain;
using YtDownloader.Infrastructure;

namespace YtDownloader.Infrastructure.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task MigrationIsIdempotentAndJobsSurviveReopeningDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"YtDownloader-db-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        var job = Job();
        job.Start();
        job.ReportProgress(65);
        try
        {
            await using (var context = new AppDbContext(options))
            {
                await context.Database.MigrateAsync();
                await context.Database.MigrateAsync();
                var repository = new EfJobRepository(context);
                await repository.AddAsync(job, default);
                await repository.SaveChangesAsync(default);
                Assert.Single(await context.Database.GetAppliedMigrationsAsync());
            }
            await using (var reopened = new AppDbContext(options))
            {
                var loaded = await new EfJobRepository(reopened).GetByIdAsync(job.Id, default);
                Assert.NotSame(job, loaded);
                Assert.Equal(JobStatus.Processing, loaded!.Status);
                Assert.Equal(65, loaded.ProgressPercent);
                Assert.Equal(job.CreatedAt, loaded.CreatedAt);
                Assert.Equal(job.Url, loaded.Url);
                Assert.Equal(job.VideoTitle, loaded.VideoTitle);
            }
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task StartupResetsInterruptedJobsAndLeavesTerminalJobsUntouched()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = Context(connection);
        await context.Database.MigrateAsync();
        var queued = Job();
        var processing = Job();
        processing.Start();
        processing.ReportProgress(75);
        var completed = Job();
        completed.Start();
        completed.Complete("file.mp4", DateTimeOffset.UtcNow.AddMinutes(30));
        var failed = Job();
        failed.Fail("Failed");
        context.AddRange(queued, processing, completed, failed);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var ids = await new DatabaseInitializer(context).InitializeAsync(default);
        Assert.Equal(new[] { queued.Id, processing.Id }.Order(), ids.Order());
        var recovered = await context.DownloadJobs.SingleAsync(job => job.Id == processing.Id);
        Assert.Equal(JobStatus.Queued, recovered.Status);
        Assert.Equal(0, recovered.ProgressPercent);
        Assert.Null(recovered.ErrorMessage);
        Assert.Equal(JobStatus.Completed, (await context.DownloadJobs.SingleAsync(job => job.Id == completed.Id)).Status);
        Assert.Equal(JobStatus.Failed, (await context.DownloadJobs.SingleAsync(job => job.Id == failed.Id)).Status);
    }

    [Fact]
    public async Task ExpiryQueryComparesUtcTicksAndHistoryIncludesOnlyRequestedJobs()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = Context(connection);
        await context.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var expires = Job();
        expires.Start();
        expires.Complete("first.mp4", now.AddMinutes(1).ToOffset(TimeSpan.FromHours(3.5)));
        var later = Job();
        later.Start();
        later.Complete("later.mp4", now.AddHours(1));
        var queued = Job();
        context.AddRange(expires, later, queued);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var repository = new EfJobRepository(context);
        Assert.Equal(expires.Id, Assert.Single(await repository.GetExpiredAsync(now.AddMinutes(2), default)).Id);
        var history = await repository.GetByIdsAsync([expires.Id, queued.Id], default);
        Assert.Equal(2, history.Count);
        Assert.DoesNotContain(history, job => job.Id == later.Id);
        Assert.True(history[0].CreatedAt >= history[1].CreatedAt);
    }

    [Fact]
    public async Task CleanupDeletesPhysicalFilesPersistsExpiredStateAndIsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = Context(connection);
        await context.Database.MigrateAsync();
        var root = Path.Combine(Path.GetTempPath(), $"YtDownloader-cleanup-{Guid.NewGuid():N}");
        var storage = new LocalFileStorage(Options.Create(new LocalFileStorageOptions { RootPath = root }));
        var job = Job();
        var output = storage.GetOutputPath(job.Id, job.Format);
        await File.WriteAllTextAsync(output, "media");
        job.Start();
        job.Complete(output, DateTimeOffset.UtcNow.AddMinutes(1));
        context.Add(job);
        await context.SaveChangesAsync();
        var repository = new EfJobRepository(context);
        var notifier = new RecordingNotifier();
        var handler = new ExpireDownloadJobsCommandHandler(repository, storage, repository, notifier,
            new FixedTimeProvider(DateTimeOffset.UtcNow.AddMinutes(2)));
        try
        {
            Assert.Equal(1, (await handler.Handle(new(), default)).Value);
            Assert.False(File.Exists(output));
            context.ChangeTracker.Clear();
            var expired = await repository.GetByIdAsync(job.Id, default);
            Assert.Equal(JobStatus.Expired, expired!.Status);
            Assert.Null(expired.FilePath);
            Assert.Equal(JobStatus.Expired, Assert.Single(notifier.Notifications).Status);
            Assert.Equal(0, (await handler.Handle(new(), default)).Value);
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public async Task StatusConcurrencyTokenPreventsOverwritingAnotherTransition()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var first = Context(connection);
        await first.Database.MigrateAsync();
        var job = Job();
        first.Add(job);
        await first.SaveChangesAsync();
        await using var second = Context(connection);
        var stale = await second.DownloadJobs.SingleAsync();
        job.Start();
        await first.SaveChangesAsync();
        stale.Fail("stale failure");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    private static AppDbContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
    private static DownloadJob Job() => new("https://youtu.be/dQw4w9WgXcQ", MediaFormat.Mp4, "1080p", "Video title");
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    private sealed class RecordingNotifier : IProgressNotifier
    {
        public List<JobStatusDto> Notifications { get; } = [];
        public Task NotifyAsync(JobStatusDto job, CancellationToken cancellationToken)
        {
            Notifications.Add(job);
            return Task.CompletedTask;
        }
    }
}
