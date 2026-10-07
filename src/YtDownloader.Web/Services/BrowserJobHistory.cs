using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;

namespace YtDownloader.Web.Services;

public sealed class BrowserJobHistory(
    IJSRuntime js, IHttpClientFactory clients, ILogger<BrowserJobHistory> logger) : IAsyncDisposable
{
    private IJSObjectReference? module;
    public IReadOnlyList<DownloadStatus> Jobs { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Error = null;
        try
        {
            module ??= await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, "./js/jobHistory.js");
            var ids = await module.InvokeAsync<Guid[]>("getIds", cancellationToken);
            if (ids.Length == 0) { Jobs = []; return; }
            using var http = clients.CreateClient("Api");
            using var response = await http.PostAsJsonAsync("api/downloads/history", new { jobIds = ids }, cancellationToken);
            if (!response.IsSuccessStatusCode) { Error = "History could not be loaded. Please try refreshing."; return; }
            Jobs = await response.Content.ReadFromJsonAsync<DownloadStatus[]>(cancellationToken) ?? [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is JSException or HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Could not load browser-local history");
            Error = "History is unavailable. Check the API connection and browser storage settings.";
        }
    }

    public async Task RememberAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            module ??= await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, "./js/jobHistory.js");
            await module.InvokeVoidAsync("addId", cancellationToken, jobId.ToString());
            await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (JSException exception)
        {
            logger.LogWarning(exception, "Could not save browser history");
            Error = "This download could not be saved to browser history.";
        }
    }

    public void Update(DownloadProgress progress) =>
        Jobs = Jobs.Select(job => job.Id == progress.JobId
            ? job with { Status = progress.Status, ProgressPercent = progress.ProgressPercent } : job).ToArray();

    public string? DownloadUrl(DownloadStatus job)
    {
        if (job.Status != "Completed" || job.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        using var client = clients.CreateClient("Api");
        return new Uri(client.BaseAddress!, $"api/downloads/{job.Id}/file").ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (module is null) return;
        try { await module.DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }
}
