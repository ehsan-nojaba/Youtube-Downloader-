namespace YtDownloader.Infrastructure;

public sealed class FfmpegOptions
{
    public const string SectionName = "Ffmpeg";
    public string Path { get; set; } = "ffmpeg";
}

public sealed class LocalFileStorageOptions
{
    public const string SectionName = "FileStorage";
    public string RootPath { get; set; } = "downloads";
    public string TempPath { get; set; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "YtDownloader");
}
