using YtDownloader.Application;

namespace YtDownloader.Api;

public static class ApiResults
{
    public static IResult Problem(Error error)
    {
        var status = error.Code switch
        {
            "Validation" or "QualityUnavailable" or "VideoTooLong" or "UnsupportedVideo" => 400,
            "NotFound" or "FileNotFound" => 404,
            "NotReady" or "InvalidStatus" => 409,
            "Expired" => 410,
            "YoutubeUnavailable" or "DownloadFailed" => 502,
            "QueueUnavailable" or "StorageUnavailable" => 503,
            _ => 500
        };
        if (error.Details is not null)
            return Results.ValidationProblem(error.Details.ToDictionary(pair => pair.Key, pair => pair.Value),
                statusCode: status, title: error.Message, extensions: new Dictionary<string, object?> { ["code"] = error.Code });
        return Results.Problem(statusCode: status, title: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }
}
