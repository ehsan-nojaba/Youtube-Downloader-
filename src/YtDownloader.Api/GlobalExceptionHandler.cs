using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace YtDownloader.Api;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception is BadHttpRequestException badRequest ? badRequest.StatusCode : 500;
        if (status >= 500)
            logger.LogError(exception, "Unhandled API exception. Trace {TraceId}", context.TraceIdentifier);
        context.Response.StatusCode = status;
        await Results.Problem(new ProblemDetails
        {
            Status = status,
            Title = status switch
            {
                400 => "The request is invalid.",
                413 => "The request body is too large.",
                415 => "The request content type is unsupported.",
                _ => "An unexpected error occurred."
            },
            Extensions = { ["traceId"] = context.TraceIdentifier }
        }).ExecuteAsync(context);
        return true;
    }
}
