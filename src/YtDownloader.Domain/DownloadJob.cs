namespace YtDownloader.Domain;

public sealed class DownloadJob
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Url { get; private set; }
    public string? VideoTitle { get; private set; }
    public MediaFormat Format { get; private set; }
    public string Quality { get; private set; }
    public JobStatus Status { get; private set; } = JobStatus.Queued;
    public int ProgressPercent { get; private set; }
    public string? FilePath { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }

    public DownloadJob(string url, MediaFormat format, string quality, string? videoTitle = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(quality);
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format));

        Url = url;
        Format = format;
        Quality = quality;
        VideoTitle = videoTitle;
    }

    public void Start()
    {
        RequireStatus(JobStatus.Queued);
        Status = JobStatus.Processing;
    }

    public void ReportProgress(int progressPercent)
    {
        RequireStatus(JobStatus.Processing);
        if (progressPercent < ProgressPercent || progressPercent > 100)
            throw new DomainException("Progress must be between the current progress and 100.");

        ProgressPercent = progressPercent;
    }

    public void Complete(string filePath, DateTimeOffset expiresAt)
    {
        RequireStatus(JobStatus.Processing);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var completedAt = DateTimeOffset.UtcNow;
        if (expiresAt <= completedAt)
            throw new DomainException("Expiration must be after completion.");

        FilePath = filePath;
        ProgressPercent = 100;
        CompletedAt = completedAt;
        ExpiresAt = expiresAt;
        Status = JobStatus.Completed;
    }

    public void Fail(string errorMessage)
    {
        if (Status is not (JobStatus.Queued or JobStatus.Processing))
            throw new DomainException($"Cannot fail a job in status {Status}.");
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        ErrorMessage = errorMessage;
        Status = JobStatus.Failed;
    }

    public void Expire()
    {
        if (Status is not (JobStatus.Completed or JobStatus.Failed))
            throw new DomainException($"Cannot expire a job in status {Status}.");

        Status = JobStatus.Expired;
        FilePath = null;
    }

    public void Requeue()
    {
        if (Status is not (JobStatus.Queued or JobStatus.Processing))
            throw new DomainException($"Cannot requeue a job in status {Status}.");
        Status = JobStatus.Queued;
        ProgressPercent = 0;
        FilePath = null;
        ErrorMessage = null;
        CompletedAt = null;
        ExpiresAt = null;
    }

    private void RequireStatus(JobStatus expected)
    {
        if (Status != expected)
            throw new DomainException($"Expected status {expected}, but job is {Status}.");
    }
}
