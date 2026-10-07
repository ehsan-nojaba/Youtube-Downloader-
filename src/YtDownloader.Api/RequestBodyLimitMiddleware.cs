using Microsoft.Extensions.Options;

namespace YtDownloader.Api;

public sealed class RequestBodyLimitMiddleware(RequestDelegate next, IOptions<ApiOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api") || !HttpMethods.IsPost(context.Request.Method))
        {
            await next(context);
            return;
        }
        var limit = options.Value.MaxRequestBodyBytes;
        if (context.Request.ContentLength > limit)
        {
            await RejectAsync(context);
            return;
        }
        // Bounded buffering also handles chunked bodies and TestServer.
        var original = context.Request.Body;
        await using var body = new MemoryStream();
        var buffer = new byte[Math.Min(limit + 1, 8192)];
        while (true)
        {
            var count = await original.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)body.Length)),
                context.RequestAborted);
            if (count == 0) break;
            await body.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
            if (body.Length > limit)
            {
                await RejectAsync(context);
                return;
            }
        }
        body.Position = 0;
        context.Request.Body = body;
        try { await next(context); }
        finally { context.Request.Body = original; }
    }
    private static Task RejectAsync(HttpContext context) =>
        Results.Problem(statusCode: 413, title: "The request body is too large.").ExecuteAsync(context);
}
