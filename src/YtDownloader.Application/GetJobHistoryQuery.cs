using FluentValidation;
using MediatR;

namespace YtDownloader.Application;

public sealed record GetJobHistoryQuery(Guid[] JobIds) : IRequest<Result<JobStatusDto[]>>;

public sealed class GetJobHistoryQueryValidator : AbstractValidator<GetJobHistoryQuery>
{
    public GetJobHistoryQueryValidator()
    {
        RuleFor(query => query.JobIds).NotNull()
            .Must(ids => ids is { Length: > 0 and <= 20 } && ids.All(id => id != Guid.Empty) && ids.Distinct().Count() == ids.Length)
            .WithMessage("Provide between 1 and 20 distinct, non-empty job IDs.");
    }
}

public sealed class GetJobHistoryQueryHandler(IJobRepository jobs)
    : IRequestHandler<GetJobHistoryQuery, Result<JobStatusDto[]>>
{
    public async Task<Result<JobStatusDto[]>> Handle(GetJobHistoryQuery request, CancellationToken cancellationToken)
    {
        var history = await jobs.GetByIdsAsync(request.JobIds, cancellationToken);
        return Result<JobStatusDto[]>.Success(history.Select(JobStatusDto.FromJob).ToArray());
    }
}
