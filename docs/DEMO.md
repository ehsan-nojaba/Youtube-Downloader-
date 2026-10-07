# Portfolio demo

## Suggested recording: 45–60 seconds

1. Show the converter's initial screen and paste a short video you own or have permission to use.
2. Load the video to show its thumbnail, title, and available qualities.
3. Select MP3 and a bitrate, then start conversion.
4. Show live progress and the completed download button.
5. Open the MP3 in a player that displays embedded cover art.
6. Return to History, then briefly show Scalar and the solution layout.

Keep the API and Web running before recording. Use a short video so the progress
and completion fit in the recording. Check that your API connection can access
the video before starting.

## Useful screenshots

- Loaded video card with thumbnail and format/quality choices.
- Completed job and a populated history section.
- Solution layout or a passing test run.

Store selected screenshots in `docs/images/` and add relative Markdown image links
to the root README after the files exist. Use your own screenshots rather than
illustrations presented as screenshots.

## Suggested GitHub description

A .NET 10 / Blazor media converter with Clean Architecture, queued background
jobs, SignalR progress, SQLite persistence, and MP3 cover art.

## Suggested repository topics

`dotnet`, `csharp`, `blazor`, `clean-architecture`, `signalr`, `ef-core`,
`sqlite`, `mediatr`, `ffmpeg`, `youtubeexplode`

## First upload

Create an empty GitHub repository, then add its URL as the local remote:

```powershell
git status
git add .
git commit -m "Initial YtDownloader application"
git remote add origin https://github.com/ehsan-nojaba/Youtube-Downloader-.git
git push -u origin main
```

The remote is already configured locally; skip `git remote add` if `origin` exists.
The repository's ignore rules
exclude build output, local settings, SQLite data, downloaded media, FFmpeg binaries,
and preview files.
