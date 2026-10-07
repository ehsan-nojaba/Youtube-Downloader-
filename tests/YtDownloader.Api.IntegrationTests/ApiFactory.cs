using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using YtDownloader.Application;
using YtDownloader.Domain;
using YtDownloader.Infrastructure;

namespace YtDownloader.Api.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly bool runWorker;
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"YtDownloader-api-{Guid.NewGuid():N}.db");
    public const string VideoUrl = "https://youtu.be/dQw4w9WgXcQ";
    public IYoutubeService Youtube { get; } = Substitute.For<IYoutubeService>();
    public IFileStorage Files { get; } = Substitute.For<IFileStorage>();
    public static VideoInfoDto Info { get; } = new("Test video", "Author", "https://example.com/image.jpg", TimeSpan.FromMinutes(3),
        [new("1080p", MediaFormat.Mp4, null, "1920x1080", 1000)],
        [new("192k", MediaFormat.Mp3, 192000, null, 500)]);

    public ApiFactory(bool runWorker = false)
    {
        this.runWorker = runWorker;
        Youtube.GetVideoInfoAsync(VideoUrl, Arg.Any<CancellationToken>()).Returns(Result<VideoInfoDto>.Success(Info));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services =>
        {
            // Endpoint tests explicitly control processing; worker behavior has its own tests.
            var worker = services.Single(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                && descriptor.ImplementationType == typeof(YtDownloader.Api.DownloadWorker));
            if (!runWorker) services.Remove(worker);
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={databasePath};Pooling=False"));
            services.RemoveAll<IYoutubeService>();
            services.RemoveAll<IFileStorage>();
            services.AddSingleton(Youtube);
            services.AddSingleton(Files);
        });
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });

    public async Task SeedAsync(params DownloadJob[] jobs)
    {
        await using var scope = Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        foreach (var job in jobs) await repository.AddAsync(job, default);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(default);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            if (File.Exists(path)) File.Delete(path);
    }
}
