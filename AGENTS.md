# Project: YtDownloader

YtDownloader is a personal media converter that converts YouTube URLs to MP4 or MP3 in selectable quality.

## Stack

.NET 10, C#, Clean Architecture, Blazor Web App, SignalR, EF Core (SQLite), MediatR, FluentValidation, YoutubeExplode, FFmpeg via CliWrap, xUnit, FluentAssertions, and NSubstitute.

## Solution layout

- `src/YtDownloader.Domain` — no dependencies.
- `src/YtDownloader.Application` — references Domain only.
- `src/YtDownloader.Infrastructure` — references Application.
- `src/YtDownloader.Api` — references Application and Infrastructure.
- `src/YtDownloader.Web` — Blazor; talks to the Api over HTTP and SignalR only.
- `tests/*` — test projects.

## Rules

- Dependencies point inward only. Never reference Infrastructure from Application or Domain.
- Use nullable reference types, file-scoped namespaces, and async/await with `CancellationToken` everywhere.
- No business logic in controllers/endpoints or Blazor components.
- Use the Result pattern or ProblemDetails for errors. Do not use exceptions for flow control.
- After every task, run `dotnet build` and `dotnet test` and fix all warnings and errors.
- Keep commits small.
- Explain what you changed at the end of every task.
