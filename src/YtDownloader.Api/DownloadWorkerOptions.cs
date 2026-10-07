namespace YtDownloader.Api;

public sealed class DownloadWorkerOptions
{
    public const string SectionName = "DownloadWorker";
    public int MaxParallelJobs { get; set; } = 2;
}
