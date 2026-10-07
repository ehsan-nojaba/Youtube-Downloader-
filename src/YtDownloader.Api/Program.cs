using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Scalar.AspNetCore;
using Serilog;
using YtDownloader.Api;
using YtDownloader.Application;
using YtDownloader.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
        .AddEnvironmentVariables().AddCommandLine(args);
}
builder.Services.AddSerilog(configuration => configuration.ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext().WriteTo.Console());
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.PostConfigure<FfmpegOptions>(options =>
{
    if (!Path.IsPathRooted(options.Path) && (options.Path.Contains('/') || options.Path.Contains('\\')))
        options.Path = Path.GetFullPath(options.Path, builder.Environment.ContentRootPath);
});
builder.Services.AddSignalR();
builder.Services.AddSingleton<IProgressNotifier, SignalRProgressNotifier>();
builder.Services.AddOptions<DownloadWorkerOptions>()
    .Bind(builder.Configuration.GetSection(DownloadWorkerOptions.SectionName))
    .Validate(options => options.MaxParallelJobs is > 0 and <= 100, "Parallel jobs must be between 1 and 100.")
    .ValidateOnStart();
builder.Services.AddHostedService<JobRecoveryWorker>();
builder.Services.AddHostedService<DownloadWorker>();
builder.Services.AddHostedService<CleanupWorker>();
builder.Services.Configure<MediaOptions>(builder.Configuration.GetSection(MediaOptions.SectionName));
builder.Services.AddOptions<ApiOptions>().Bind(builder.Configuration.GetSection(ApiOptions.SectionName))
    .Validate(options => options.MaxRequestBodyBytes is > 0 and <= 1_048_576 &&
        options.RateLimitPermitLimit > 0 && options.RateLimitWindowSeconds > 0 &&
        options.BlazorOrigins.Length > 0 && options.BlazorOrigins.All(origin =>
            Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.Scheme is "https" or "http") &&
            uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)),
        "Provide valid API limits and explicit Blazor origins.")
    .ValidateOnStart();
var apiOptions = builder.Configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>() ?? new();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = apiOptions.MaxRequestBodyBytes);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddCors(options => options.AddPolicy("Blazor", policy => policy
    .WithOrigins(apiOptions.BlazorOrigins).AllowAnyMethod().AllowAnyHeader().AllowCredentials()
    .WithExposedHeaders("Content-Disposition")));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("per-ip", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = apiOptions.RateLimitPermitLimit,
            Window = TimeSpan.FromSeconds(apiOptions.RateLimitWindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        await Results.Problem(statusCode: 429, title: "Too many requests.").ExecuteAsync(context.HttpContext);
    };
});
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();

var app = builder.Build();
app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();
app.UseCors("Blazor");
app.UseRateLimiter();
app.UseMiddleware<RequestBodyLimitMiddleware>();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthChecks("/health");
app.MapMediaEndpoints();
app.MapHub<DownloadHub>("/hubs/downloads");
app.Run();

public partial class Program;
