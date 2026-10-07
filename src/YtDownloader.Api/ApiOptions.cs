namespace YtDownloader.Api;

public sealed class ApiOptions
{
    public const string SectionName = "Api";
    public string[] BlazorOrigins { get; set; } = ["http://localhost:5147", "https://localhost:7172"];
    public int RateLimitPermitLimit { get; set; } = 30;
    public int RateLimitWindowSeconds { get; set; } = 60;
    public int MaxRequestBodyBytes { get; set; } = 16 * 1024;
}
