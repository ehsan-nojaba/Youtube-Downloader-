using Microsoft.EntityFrameworkCore;
using YtDownloader.Domain;

namespace YtDownloader.Infrastructure;

public sealed class DatabaseInitializer(AppDbContext context)
{
    public async Task<IReadOnlyList<Guid>> InitializeAsync(CancellationToken cancellationToken)
    {
        await context.Database.MigrateAsync(cancellationToken);
        var pending = await context.DownloadJobs
            .Where(job => job.Status == JobStatus.Queued || job.Status == JobStatus.Processing)
            .OrderBy(job => job.CreatedAt).ToListAsync(cancellationToken);
        foreach (var job in pending) job.Requeue();
        await context.SaveChangesAsync(cancellationToken);
        return pending.Select(job => job.Id).ToArray();
    }
}
