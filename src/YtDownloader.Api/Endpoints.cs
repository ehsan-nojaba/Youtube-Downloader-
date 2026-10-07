using MediatR;
using YtDownloader.Application;

namespace YtDownloader.Api;

public sealed record CreateDownloadResponse(Guid JobId);

public static class Endpoints
{
    public static void MapMediaEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireRateLimiting("per-ip");
        var videos = api.MapGroup("/videos").WithTags("Videos");
        videos.MapGet("/info", async (string url, IMediator mediator, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(new GetVideoInfoQuery(url), cancellationToken);
            return result.IsSuccess ? Results.Ok(result.Value) : ApiResults.Problem(result.Error!);
        }).WithName("GetVideoInfo").WithSummary("Get video metadata and available output qualities.")
            .Produces<VideoInfoDto>().ProducesValidationProblem().ProducesProblem(502);
        var downloads = api.MapGroup("/downloads").WithTags("Downloads");
        downloads.MapPost("/history", async (GetJobHistoryQuery query, IMediator mediator, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(query, cancellationToken);
            return result.IsSuccess ? Results.Ok(result.Value) : ApiResults.Problem(result.Error!);
        }).WithName("GetDownloadHistory").WithSummary("Get recent jobs for the supplied browser-local job IDs.")
            .Produces<JobStatusDto[]>().ProducesValidationProblem();
        downloads.MapPost("", async (CreateDownloadJobCommand command, IMediator mediator, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(command, cancellationToken);
            return result.IsSuccess
                ? Results.Accepted($"/api/downloads/{result.Value}", new CreateDownloadResponse(result.Value))
                : ApiResults.Problem(result.Error!);
        }).WithName("CreateDownload").WithSummary("Queue an MP4 or MP3 download.")
            .Produces<CreateDownloadResponse>(202).ProducesValidationProblem().ProducesProblem(503);
        downloads.MapGet("/{id:guid}", async (Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(new GetJobStatusQuery(id), cancellationToken);
            return result.IsSuccess ? Results.Ok(result.Value) : ApiResults.Problem(result.Error!);
        }).WithName("GetDownloadStatus").Produces<JobStatusDto>().ProducesProblem(404).ProducesValidationProblem();
        downloads.MapGet("/{id:guid}/file", async (Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(new GetDownloadFileQuery(id), cancellationToken);
            return result.IsSuccess
                ? Results.File(result.Value!.Stream, result.Value.ContentType, result.Value.FileName, enableRangeProcessing: true)
                : ApiResults.Problem(result.Error!);
        }).WithName("GetDownloadFile").WithSummary("Download a completed file.")
            .Produces(200, contentType: "video/mp4").Produces(200, contentType: "audio/mpeg")
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(410);
    }
}
