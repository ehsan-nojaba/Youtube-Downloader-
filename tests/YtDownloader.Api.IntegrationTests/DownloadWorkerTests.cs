using System.Collections.Concurrent;
using System.Threading.Channels;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using YtDownloader.Api;
using YtDownloader.Application;
using YtDownloader.Domain;
using YtDownloader.Infrastructure;

namespace YtDownloader.Api.IntegrationTests;

public sealed class DownloadWorkerTests
{
    [Fact]
    public async Task ProcessesAtMostConfiguredConcurrencyWithFreshScopes()
    {
        var queue = new FakeQueue();
        var mediator = Substitute.For<IMediator>();
        var repository = new FakeJobRepository();
        var release = NewSignal();
        var twoStarted = NewSignal();
        var allFinished = NewSignal();
        var active = 0;
        var started = 0;
        var finished = 0;
        var maximum = 0;
        var scopeCount = 0;
        var disposedScopes = 0;
        mediator.Send(Arg.Any<ProcessDownloadJobCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var current = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximum, current);
            if (Interlocked.Increment(ref started) == 2) twoStarted.TrySetResult();
            await release.Task.WaitAsync(call.Arg<CancellationToken>());
            Interlocked.Decrement(ref active);
            if (Interlocked.Increment(ref finished) == 4) allFinished.TrySetResult();
            return Result<bool>.Success(true);
        });
        var services = Services(mediator, repository);
        services.AddScoped(_ =>
        {
            Interlocked.Increment(ref scopeCount);
            return new ScopeProbe(() => Interlocked.Increment(ref disposedScopes));
        });
        services.AddScoped<IMediator>(provider =>
        {
            _ = provider.GetRequiredService<ScopeProbe>();
            return mediator;
        });
        await using var provider = services.BuildServiceProvider();
        using var worker = Worker(queue, provider, 2);
        for (var index = 0; index < 4; index++) await queue.EnqueueAsync(Guid.NewGuid(), default);
        await worker.StartAsync(default);
        try
        {
            await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, active);
            Assert.Equal(2, queue.Dequeued);
            release.TrySetResult();
            await allFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.TrySetResult();
            await StopAsync(worker);
        }
        Assert.Equal(2, maximum);
        Assert.Equal(4, scopeCount);
        Assert.Equal(scopeCount, disposedScopes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarksFailuresAndContinuesWithNextJob(bool throws)
    {
        var queue = new FakeQueue();
        var mediator = Substitute.For<IMediator>();
        var repository = new FakeJobRepository();
        var failedJob = NewJob();
        var nextJob = NewJob();
        await repository.AddAsync(failedJob, default);
        await repository.AddAsync(nextJob, default);
        var finished = NewSignal();
        mediator.Send(Arg.Any<ProcessDownloadJobCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var id = call.Arg<ProcessDownloadJobCommand>().JobId;
            if (id == failedJob.Id)
            {
                failedJob.Start();
                return throws
                    ? Task.FromException<Result<bool>>(new IOException("secret path"))
                    : Task.FromResult(Result<bool>.Failure(new("MediaUnavailable", "The media is unavailable.")));
            }
            finished.TrySetResult();
            return Task.FromResult(Result<bool>.Success(true));
        });
        var notifier = Substitute.For<IProgressNotifier>();
        await using var provider = Services(mediator, repository, notifier).BuildServiceProvider();
        using var worker = Worker(queue, provider, 1);
        await queue.EnqueueAsync(failedJob.Id, default);
        await queue.EnqueueAsync(nextJob.Id, default);
        await worker.StartAsync(default);
        try { await finished.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { await StopAsync(worker); }
        Assert.Equal(JobStatus.Failed, failedJob.Status);
        Assert.NotEmpty(failedJob.ErrorMessage!);
        Assert.DoesNotContain("secret", failedJob.ErrorMessage!);
        await notifier.Received(1).NotifyAsync(
            Arg.Is<JobStatusDto>(job => job.Id == failedJob.Id && job.Status == JobStatus.Failed), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShutdownCancelsProcessingAndMarksJobFailed()
    {
        var queue = new FakeQueue();
        var mediator = Substitute.For<IMediator>();
        var repository = new FakeJobRepository();
        var job = NewJob();
        await repository.AddAsync(job, default);
        var started = NewSignal();
        mediator.Send(Arg.Any<ProcessDownloadJobCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            job.Start();
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
            return Result<bool>.Success(true);
        });
        await using var provider = Services(mediator, repository).BuildServiceProvider();
        using var worker = Worker(queue, provider, 1);
        await queue.EnqueueAsync(job.Id, default);
        await worker.StartAsync(default);
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { await StopAsync(worker); }
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("stopping", job.ErrorMessage!);
    }

    [Fact]
    public async Task ShutdownWhileWaitingForQueueIsClean()
    {
        var queue = new FakeQueue();
        var mediator = Substitute.For<IMediator>();
        await using var provider = Services(mediator, new FakeJobRepository()).BuildServiceProvider();
        using var worker = Worker(queue, provider, 2);
        await worker.StartAsync(default);
        await queue.TwoReaders.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await StopAsync(worker);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
        await mediator.DidNotReceiveWithAnyArgs().Send(default(ProcessDownloadJobCommand)!, default);
    }

    [Fact]
    public async Task DuplicateQueueEntriesNeverProcessSameJobConcurrently()
    {
        var queue = new FakeQueue();
        var mediator = Substitute.For<IMediator>();
        var release = NewSignal();
        mediator.Send(Arg.Any<ProcessDownloadJobCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await release.Task.WaitAsync(call.Arg<CancellationToken>());
            return Result<bool>.Success(true);
        });
        await using var provider = Services(mediator, new FakeJobRepository()).BuildServiceProvider();
        using var worker = Worker(queue, provider, 2);
        var id = Guid.NewGuid();
        await queue.EnqueueAsync(id, default);
        await queue.EnqueueAsync(id, default);
        await worker.StartAsync(default);
        try
        {
            await queue.ThirdRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await mediator.Received(1).Send(Arg.Is<ProcessDownloadJobCommand>(command => command.JobId == id), Arg.Any<CancellationToken>());
        }
        finally
        {
            release.TrySetResult();
            await StopAsync(worker);
        }
    }

    [Fact]
    public async Task WorkerDoesNotFailAlreadyCompletedJobs()
    {
        var queue = new FakeQueue();
        var mediator = Substitute.For<IMediator>();
        var repository = new FakeJobRepository();
        var job = NewJob();
        job.Start();
        job.Complete("output.mp4", DateTimeOffset.UtcNow.AddHours(1));
        await repository.AddAsync(job, default);
        var notified = NewSignal();
        mediator.Send(Arg.Any<ProcessDownloadJobCommand>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            notified.TrySetResult();
            return Task.FromResult(Result<bool>.Failure(new("InvalidStatus", "Already completed")));
        });
        await using var provider = Services(mediator, repository).BuildServiceProvider();
        using var worker = Worker(queue, provider, 1);
        await queue.EnqueueAsync(job.Id, default);
        await worker.StartAsync(default);
        try { await notified.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { await StopAsync(worker); }
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Null(job.ErrorMessage);
    }

    private static ServiceCollection Services(IMediator mediator, FakeJobRepository repository, IProgressNotifier? notifier = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IMediator>(_ => mediator);
        services.AddSingleton<IJobRepository>(repository);
        services.AddSingleton<IUnitOfWork>(repository);
        services.AddSingleton(notifier ?? Substitute.For<IProgressNotifier>());
        return services;
    }

    private static DownloadWorker Worker(FakeQueue queue, ServiceProvider provider, int parallel) =>
        new(queue, provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DownloadWorkerOptions { MaxParallelJobs = parallel }), NullLogger<DownloadWorker>.Instance);
    private static DownloadJob NewJob() => new(ApiFactory.VideoUrl, MediaFormat.Mp4, "1080p");
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task StopAsync(DownloadWorker worker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StopAsync(timeout.Token);
    }

    private sealed class ScopeProbe(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class FakeQueue : IJobQueue
    {
        private readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>();
        private int reads;
        private int dequeued;
        public int Dequeued => Volatile.Read(ref dequeued);
        public TaskCompletionSource TwoReaders { get; } = NewSignal();
        public TaskCompletionSource ThirdRead { get; } = NewSignal();
        public async Task EnqueueAsync(Guid jobId, CancellationToken cancellationToken) =>
            await channel.Writer.WriteAsync(jobId, cancellationToken);
        public async Task<Guid> DequeueAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref reads);
            if (count == 2) TwoReaders.TrySetResult();
            if (count == 3) ThirdRead.TrySetResult();
            var id = await channel.Reader.ReadAsync(cancellationToken);
            Interlocked.Increment(ref dequeued);
            return id;
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            int current;
            do { current = Volatile.Read(ref location); if (current >= value) return; }
            while (Interlocked.CompareExchange(ref location, value, current) != current);
        }
    }
}

