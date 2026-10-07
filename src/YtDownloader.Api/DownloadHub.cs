using MediatR;
using Microsoft.AspNetCore.SignalR;
using YtDownloader.Application;

namespace YtDownloader.Api;

public sealed record DownloadProgress(Guid JobId, string Status, int ProgressPercent)
{
    public static DownloadProgress FromJob(JobStatusDto job) => new(job.Id, job.Status.ToString(), job.ProgressPercent);
}

public sealed class DownloadHub(IMediator mediator) : Hub
{
    public async Task<bool> JoinJob(Guid jobId)
    {
        if (jobId == Guid.Empty) return false;
        await Groups.AddToGroupAsync(Context.ConnectionId, jobId.ToString(), Context.ConnectionAborted);
        // Snapshot after joining covers completion that occurred before the client connected.
        var result = await mediator.Send(new GetJobStatusQuery(jobId), Context.ConnectionAborted);
        if (!result.IsSuccess)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, jobId.ToString(), Context.ConnectionAborted);
            return false;
        }
        await Clients.Caller.SendAsync("JobProgress", DownloadProgress.FromJob(result.Value!), Context.ConnectionAborted);
        return true;
    }

    public Task LeaveJob(Guid jobId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, jobId.ToString(), Context.ConnectionAborted);
}

public sealed class SignalRProgressNotifier(
    IHubContext<DownloadHub> hub, ILogger<SignalRProgressNotifier> logger) : IProgressNotifier
{
    public async Task NotifyAsync(JobStatusDto job, CancellationToken cancellationToken)
    {
        try
        {
            await hub.Clients.Group(job.Id.ToString()).SendAsync(
                "JobProgress", DownloadProgress.FromJob(job), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // A notification failure must not turn a successful media conversion into a failed job.
            logger.LogWarning(exception, "Could not publish progress for job {JobId}", job.Id);
        }
    }
}
