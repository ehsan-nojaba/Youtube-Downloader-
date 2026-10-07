using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace YtDownloader.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddOptions<MediaOptions>()
            .Validate(options => options.MaxVideoDurationMinutes > 0, "Maximum video duration must be positive.")
            .Validate(options => options.FileRetentionMinutes > 0, "File retention must be positive.")
            .ValidateOnStart();
        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssemblyContaining<GetVideoInfoQuery>();
            configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });
        services.AddValidatorsFromAssemblyContaining<GetVideoInfoQueryValidator>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
