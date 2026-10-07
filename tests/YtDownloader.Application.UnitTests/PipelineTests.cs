using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using YtDownloader.Application;

namespace YtDownloader.Application.UnitTests;

public sealed class PipelineTests
{
    [Fact]
    public async Task ValidationFailureStopsHandlerAndReturnsFieldErrors()
    {
        var next = Substitute.For<RequestHandlerDelegate<Result<VideoInfoDto>>>();
        var behavior = new ValidationBehavior<GetVideoInfoQuery, Result<VideoInfoDto>>([new GetVideoInfoQueryValidator()]);
        var result = await behavior.Handle(new("https://evil.com"), next, default);
        Assert.Equal("Validation", result.Error!.Code);
        Assert.Contains("Url", result.Error.Details!.Keys);
        await next.DidNotReceive().Invoke();
    }

    [Fact]
    public async Task ValidRequestRunsHandler()
    {
        var next = Substitute.For<RequestHandlerDelegate<Result<VideoInfoDto>>>();
        var expected = Result<VideoInfoDto>.Failure(new("Unavailable", "Unavailable"));
        next().Returns(expected);
        var behavior = new ValidationBehavior<GetVideoInfoQuery, Result<VideoInfoDto>>([new GetVideoInfoQueryValidator()]);
        Assert.Same(expected, await behavior.Handle(new(HandlerTests.Url), next, default));
        await next.Received(1).Invoke();
    }

    [Fact]
    public async Task ValidationReceivesCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        var validator = Substitute.For<IValidator<GetVideoInfoQuery>>();
        validator.ValidateAsync(Arg.Any<GetVideoInfoQuery>(), cancellation.Token)
            .Returns(new FluentValidation.Results.ValidationResult());
        var next = Substitute.For<RequestHandlerDelegate<Result<VideoInfoDto>>>();
        next().Returns(Result<VideoInfoDto>.Failure(new("Unavailable", "Unavailable")));
        await new ValidationBehavior<GetVideoInfoQuery, Result<VideoInfoDto>>([validator])
            .Handle(new(HandlerTests.Url), next, cancellation.Token);
        await validator.Received(1).ValidateAsync(Arg.Any<GetVideoInfoQuery>(), cancellation.Token);
    }

    [Fact]
    public async Task AddApplicationRegistersHandlersAndPipeline()
    {
        var youtube = Substitute.For<IYoutubeService>();
        var services = new ServiceCollection().AddApplication();
        services.AddSingleton(youtube);
        services.AddSingleton(Substitute.For<IJobRepository>());
        services.AddSingleton(Substitute.For<IUnitOfWork>());
        services.AddSingleton(Substitute.For<IJobQueue>());
        services.AddSingleton(Substitute.For<IFileStorage>());
        services.AddSingleton(Substitute.For<IMediaConverter>());
        services.AddSingleton(Substitute.For<IProgressNotifier>());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        Assert.Equal("Validation", (await mediator.Send(new GetVideoInfoQuery("invalid"))).Error!.Code);
        Assert.Equal("Validation", (await mediator.Send(new CreateDownloadJobCommand("invalid", Domain.MediaFormat.Mp4, "192k"))).Error!.Code);
        Assert.Equal("Validation", (await mediator.Send(new GetJobStatusQuery(Guid.Empty))).Error!.Code);
        Assert.Equal("Validation", (await mediator.Send(new GetDownloadFileQuery(Guid.Empty))).Error!.Code);
        Assert.Equal("Validation", (await mediator.Send(new ProcessDownloadJobCommand(Guid.Empty))).Error!.Code);
        await youtube.DidNotReceiveWithAnyArgs().GetVideoInfoAsync(default!, default);
        youtube.GetVideoInfoAsync(HandlerTests.Url, Arg.Any<CancellationToken>())
            .Returns(Result<VideoInfoDto>.Failure(new("Unavailable", "Unavailable")));
        Assert.Equal("Unavailable", (await mediator.Send(new GetVideoInfoQuery(HandlerTests.Url))).Error!.Code);
    }
}


