namespace YtDownloader.Infrastructure;

public sealed class JobQueueOptions
{
    public const string SectionName = "JobQueue";
    public int Capacity { get; set; } = 100;
}
