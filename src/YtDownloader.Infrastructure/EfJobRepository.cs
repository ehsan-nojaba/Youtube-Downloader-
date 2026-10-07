using Microsoft.EntityFrameworkCore;
using YtDownloader.Application;
using YtDownloader.Domain;

namespace YtDownloader.Infrastructure;

public sealed class EfJobRepository(AppDbContext context) : IJobRepository, IUnitOfWork
{
    public Task<DownloadJob?> GetByIdAsync(Guid jobId, CancellationToken cancellationToken) =>
        context.DownloadJobs.SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken);
    public async Task AddAsync(DownloadJob job, CancellationToken cancellationToken) =>
        await context.DownloadJobs.AddAsync(job, cancellationToken);
    public async Task<IReadOnlyList<DownloadJob>> GetExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await context.DownloadJobs.Where(job => (job.Status == JobStatus.Completed || job.Status == JobStatus.Failed) &&
            job.ExpiresAt != null && job.ExpiresAt <= now).OrderBy(job => job.ExpiresAt).ToListAsync(cancellationToken);
    public async Task SaveChangesAsync(CancellationToken cancellationToken) =>
        await context.SaveChangesAsync(cancellationToken);
    public async Task<IReadOnlyList<DownloadJob>> GetByIdsAsync(Guid[] jobIds, CancellationToken cancellationToken) =>
        await context.DownloadJobs.AsNoTracking().Where(job => jobIds.Contains(job.Id))
            .OrderByDescending(job => job.CreatedAt).ToListAsync(cancellationToken);
}
