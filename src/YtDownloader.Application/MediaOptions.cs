namespace YtDownloader.Application;

public sealed class MediaOptions
{
    public const string SectionName = "Media";
    public int MaxVideoDurationMinutes { get; set; } = 120;
    public int FileRetentionMinutes { get; set; } = 30;
}
