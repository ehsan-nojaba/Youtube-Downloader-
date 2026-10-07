using System.Threading.Channels;
using Microsoft.Extensions.Options;
using YtDownloader.Application;

namespace YtDownloader.Infrastructure;

// Process-local queue; waits for space instead of silently dropping jobs.
public sealed class InMemoryJobQueue : IJobQueue
{
    private readonly Channel<Guid> channel;

    public InMemoryJobQueue(IOptions<JobQueueOptions> options) =>
        channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(options.Value.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    public async Task EnqueueAsync(Guid jobId, CancellationToken cancellationToken) =>
        await channel.Writer.WriteAsync(jobId, cancellationToken);
    public async Task<Guid> DequeueAsync(CancellationToken cancellationToken) =>
        await channel.Reader.ReadAsync(cancellationToken);
}
