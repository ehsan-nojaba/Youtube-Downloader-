using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace YtDownloader.Web.Services;

public sealed record QualityOption(string Label, string Format);
public sealed record VideoInfo(string Title, string Author, string ThumbnailUrl, TimeSpan Duration, QualityOption[] VideoQualities, QualityOption[] AudioQualities);
public sealed record DownloadStatus(Guid Id, string Status, int ProgressPercent, string? ErrorMessage,  string? VideoTitle = null, string? Format = null, string? Quality = null, DateTimeOffset? CreatedAt = null, DateTimeOffset? ExpiresAt = null);
public sealed record DownloadProgress(Guid JobId, string Status, int ProgressPercent);
public sealed record CreatedJob(Guid JobId);

public sealed class DownloadSession(HttpClient http, ILogger<DownloadSession> logger, BrowserJobHistory history) : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? monitoringCancellation;
    private Task? monitoring;
    private HubConnection? connection;
    private volatile bool joined;
    private int disposed;
    public event Action? Changed;
    public BrowserJobHistory History => history;
    public async Task RefreshHistoryAsync(CancellationToken cancellationToken)
    {
        await history.RefreshAsync(cancellationToken);
        Changed?.Invoke();
    }
    public string Url { get; set; } = "";
    public string Format { get; private set; } = "Mp4";
    public string Quality { get; set; } = "";
    public VideoInfo? Video { get; private set; }
    public DownloadStatus? Job { get; private set; }
    public string? Error { get; private set; }
    public bool Busy { get; private set; }
    public bool LoadingVideo { get; private set; }
    public bool IsTerminal => Job?.Status is "Completed" or "Failed" or "Expired";
    public bool IsActive => Job is not null && !IsTerminal;
    public bool UsesPolling => IsActive && !joined;
    public QualityOption[] Qualities => (Format == "Mp4" ? Video?.VideoQualities : Video?.AudioQualities) ?? [];
    public string? DownloadUrl => Job?.Status == "Completed" ? new Uri(http.BaseAddress!, $"api/downloads/{Job.Id}/file").ToString() : null;

    public void ChangeFormat(string format)
    {
        Format = format;
        Quality = Qualities.FirstOrDefault()?.Label ?? "";
    }

    public async Task LoadVideoInfoAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        Busy = true;
        LoadingVideo = true;
        Error = null;
        Video = null;
        Changed?.Invoke();
        try
        {
            using var response = await http.GetAsync("api/videos/info?url=" + Uri.EscapeDataString(Url), linked.Token);
            if (!response.IsSuccessStatusCode) { Error = await ReadErrorAsync(response, linked.Token); return; }
            Video = await response.Content.ReadFromJsonAsync<VideoInfo>(linked.Token);
            Quality = Qualities.FirstOrDefault()?.Label ?? "";
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Could not retrieve video information");
            Error = "Video information could not be loaded. Please try again.";
        }
        finally { Busy = false; LoadingVideo = false; Changed?.Invoke(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        Busy = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            using var response = await http.PostAsJsonAsync("api/downloads", new { url = Url, format = Format, quality = Quality }, linked.Token);
            if (!response.IsSuccessStatusCode) { Error = await ReadErrorAsync(response, linked.Token); return; }
            var created = await response.Content.ReadFromJsonAsync<CreatedJob>(linked.Token);
            if (created is null) { Error = "The API returned no job ID."; return; }
            await StopMonitoringAsync();
            Job = new(created.JobId, "Queued", 0, null);
            await history.RememberAsync(created.JobId, linked.Token);
            monitoringCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var monitoringToken = monitoringCancellation.Token;
            connection = new HubConnectionBuilder().WithUrl(new Uri(http.BaseAddress!, "hubs/downloads"))
                .WithAutomaticReconnect().Build();
            connection.On<DownloadProgress>("JobProgress", async progress =>
            {
                if (Job?.Id != progress.JobId || IsTerminal) return;
                if (Job.Status == "Processing" && progress.Status == "Queued") return;
                Job = Job with { Status = progress.Status, ProgressPercent = Math.Max(Job.ProgressPercent, progress.ProgressPercent) };
                history.Update(progress);
                Changed?.Invoke();
                if (IsTerminal)
                {
                    await RefreshAsync(monitoringToken);
                }
            });
            connection.Reconnecting += _ => { joined = false; Changed?.Invoke(); return Task.CompletedTask; };
            connection.Reconnected += _ => { joined = false; Changed?.Invoke(); return Task.CompletedTask; };
            connection.Closed += _ => { joined = false; Changed?.Invoke(); return Task.CompletedTask; };
            monitoring = MonitorAsync(monitoringToken);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Could not create download");
            Error = "The download could not be started. Please try again.";
        }
        finally { Busy = false; Changed?.Invoke(); }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            do
            {
                if (connection!.State == HubConnectionState.Disconnected)
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    attempt.CancelAfter(TimeSpan.FromSeconds(3));
                    try { await connection.StartAsync(attempt.Token); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) { logger.LogDebug(exception, "SignalR unavailable; using polling"); }
                }
                if (connection.State == HubConnectionState.Connected && !joined)
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    attempt.CancelAfter(TimeSpan.FromSeconds(3));
                    try { joined = await connection.InvokeAsync<bool>("JoinJob", Job!.Id, attempt.Token); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) { logger.LogDebug(exception, "Could not join job group; using polling"); }
                    Changed?.Invoke();
                }
                if (!joined) await RefreshAsync(cancellationToken);
                if (IsTerminal) return;
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var jobId = Job!.Id;
            using var response = await http.GetAsync($"api/downloads/{jobId}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Error = await ReadErrorAsync(response, cancellationToken);
                Changed?.Invoke();
                return;
            }
            var status = await response.Content.ReadFromJsonAsync<DownloadStatus>(cancellationToken);
            if (status is not null && status.Id == Job.Id && (!IsTerminal || status.Status == Job.Status) &&
                !(Job.Status == "Processing" && status.Status == "Queued"))
            {
                Job = status with { ProgressPercent = Math.Max(Job.ProgressPercent, status.ProgressPercent) };
                history.Update(new(Job.Id, Job.Status, Job.ProgressPercent));
                Error = status.ErrorMessage;
                if (IsTerminal) await history.RefreshAsync(cancellationToken);
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogDebug(exception, "Polling failed; will retry");
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("errors", out var errors))
                return string.Join(" ", errors.EnumerateObject().SelectMany(field => field.Value.EnumerateArray())
                    .Select(message => message.GetString()));
            if (document.RootElement.TryGetProperty("title", out var title))
                return title.GetString() ?? "The request failed.";
        }
        catch (JsonException) { }
        return $"The request failed ({(int)response.StatusCode}).";
    }

    private async Task StopMonitoringAsync()
    {
        if (monitoringCancellation is not null) await monitoringCancellation.CancelAsync();
        if (monitoring is not null) await monitoring;
        if (connection is not null) await connection.DisposeAsync();
        monitoringCancellation?.Dispose();
        monitoring = null;
        connection = null;
        joined = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifetime.CancelAsync();
        await StopMonitoringAsync();
        lifetime.Dispose();
    }
}
