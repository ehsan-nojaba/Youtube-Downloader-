using MediatR;
using YtDownloader.Domain;

namespace YtDownloader.Application;

public sealed record GetVideoInfoQuery(string Url) : IRequest<Result<VideoInfoDto>>;
public sealed record CreateDownloadJobCommand(string Url, MediaFormat Format, string Quality) : IRequest<Result<Guid>>;
public sealed record GetJobStatusQuery(Guid JobId) : IRequest<Result<JobStatusDto>>;
public sealed record GetDownloadFileQuery(Guid JobId) : IRequest<Result<DownloadFileDto>>;
public sealed record ProcessDownloadJobCommand(Guid JobId) : IRequest<Result<bool>>;
