# LinkedIn post draft

I built YtDownloader, a personal media converter with .NET 10 and Blazor.

The app turns a YouTube video link into an MP4 or MP3 file with selectable
quality. It includes thumbnail previews, MP3 cover art, live progress, and
browser-local download history.

The most interesting part was the background pipeline:

- Clean Architecture separates domain rules, application use cases, and infrastructure.
- A bounded channel queues jobs, with configurable parallel processing.
- SignalR delivers progress updates; HTTP polling takes over if the connection drops.
- EF Core and SQLite persist jobs and recover interrupted work after restart.
- A cleanup worker expires temporary downloads and removes their files.

I also added validation, ProblemDetails responses, rate limiting, structured
logging, and tests for job transitions, persistence, workers, API endpoints,
and real FFmpeg conversions.

This project gave me practical experience connecting a responsive UI to an
asynchronous processing workflow, including cancellation, failures, and recovery.

Repository: https://github.com/ehsan-nojaba/Youtube-Downloader-

[Attach a screenshot or a short demo recording.]

#DotNet #CSharp #Blazor #CleanArchitecture #SignalR #EFCore #SoftwareDevelopment
