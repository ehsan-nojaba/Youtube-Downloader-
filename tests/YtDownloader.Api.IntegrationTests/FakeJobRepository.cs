using System.Collections.Concurrent;
using YtDownloader.Application;
using YtDownloader.Domain;

namespace YtDownloader.Api.IntegrationTests;

internal sealed class FakeJobRepository : IJobRepository, IUnitOfWork
{
    private readonly ConcurrentDictionary<Guid, DownloadJob> jobs = new();
    public Task<DownloadJob?> GetByIdAsync(Guid jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        jobs.TryGetValue(jobId, out var job);
        return Task.FromResult(job);
    }
    public Task AddAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        jobs[job.Id] = job;
        return Task.CompletedTask;
    }
    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<DownloadJob>> GetExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DownloadJob>>(jobs.Values.Where(job => job.ExpiresAt <= now).ToArray());
    public Task<IReadOnlyList<DownloadJob>> GetByIdsAsync(Guid[] jobIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DownloadJob>>(jobs.Values.Where(job => jobIds.Contains(job.Id)).ToArray());
}
