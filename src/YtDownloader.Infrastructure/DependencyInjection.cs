using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using YoutubeExplode;
using YtDownloader.Application;

namespace YtDownloader.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JobQueueOptions>().Bind(configuration.GetSection(JobQueueOptions.SectionName))
            .Validate(options => options.Capacity > 0, "Queue capacity must be positive.").ValidateOnStart();
        services.AddOptions<FfmpegOptions>().Bind(configuration.GetSection(FfmpegOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Path), "An FFmpeg executable path is required.")
            .ValidateOnStart();
        services.AddOptions<LocalFileStorageOptions>().Bind(configuration.GetSection(LocalFileStorageOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.RootPath) && !string.IsNullOrWhiteSpace(options.TempPath),
                "Storage and temporary paths are required.")
            .ValidateOnStart();
        services.AddSingleton<YoutubeClient>();
        services.AddHttpClient("YoutubeThumbnails");
        services.AddTransient<IYoutubeService>(provider => new YoutubeService(
            provider.GetRequiredService<YoutubeClient>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalFileStorageOptions>>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<YoutubeService>>(),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("YoutubeThumbnails")));
        services.AddTransient<IMediaConverter, FfmpegConverter>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(configuration.GetConnectionString("Jobs") ?? "Data Source=YtDownloader.db"));
        services.AddScoped<EfJobRepository>();
        services.AddScoped<IJobRepository>(provider => provider.GetRequiredService<EfJobRepository>());
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<EfJobRepository>());
        services.AddScoped<DatabaseInitializer>();
        services.AddSingleton<IJobQueue, InMemoryJobQueue>();
        return services;
    }
}
