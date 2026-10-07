# YtDownloader API

Run with `dotnet run --project src/YtDownloader.Api --launch-profile https`.

- `/scalar/v1`: interactive Scalar API reference.
- `/openapi/v1.json`: OpenAPI document.
- `/health`: application liveness check.
- `GET /api/videos/info?url=...`: video metadata and output qualities.
- `POST /api/downloads`: queue a download and return 202 with `jobId` and a status Location.
- `GET /api/downloads/{id}`: job status.
- `GET /api/downloads/{id}/file`: completed file attachment, with range support.
- `POST /api/downloads/history`: statuses for up to 20 supplied job IDs.

Example JSON body:

```json
{ "url": "https://youtu.be/dQw4w9WgXcQ", "format": "Mp3", "quality": "192k" }
```

Configure these keys in appsettings or environment variables:

| Key | Default |
| --- | --- |
| `Media:MaxVideoDurationMinutes` | 120 (2 hours) |
| `Media:FileRetentionMinutes` | 30 minutes after completion |
| `ConnectionStrings:Jobs` | Data Source=YtDownloader.db |
| `Api:BlazorOrigins` | http://localhost:5147, https://localhost:7172 |
| `Api:RateLimitPermitLimit` | 30 requests per IP |
| `Api:RateLimitWindowSeconds` | 60 |
| `Api:MaxRequestBodyBytes` | 16384 |
| `JobQueue:Capacity` | 100 |
| `DownloadWorker:MaxParallelJobs` | 2 |

Duration checks run in the application handlers for both metadata and job creation.
Errors use ProblemDetails; validation errors include an `errors` dictionary.
Serilog writes to the console and reads log levels from the `Serilog` configuration section.
Rate limiting uses the connection IP; untrusted forwarded headers are ignored.

Jobs are persisted in SQLite. Startup applies migrations and resets Queued/Processing
jobs to Queued with progress reset, then restores them to the bounded queue.
The queue waits for space when full. The hosted worker uses a fresh service scope per job and
runs up to the configured number in parallel. Failed or cancelled active jobs are
marked Failed. Configure FFmpeg before running real conversions.

FFmpeg defaults to `ffmpeg` on PATH. Override `Ffmpeg:Path` with an executable
path through environment variables or an ignored development-only
`appsettings.Local.json`. Relative executable paths resolve from the API content
root. The local portable build under `.tools/` is ignored by Git.

MP3 downloads embed the video's thumbnail as an ID3 front-cover image. The
thumbnail is downloaded alongside the audio and removed with other temporary
source files after conversion.

Stop `dotnet run` before rebuilding the project it is running: Windows locks
loaded executables and assemblies. For a preview that permits normal builds, use
a separate published output folder (`.preview/`, ignored by Git) and run its
DLL instead of the executable in `bin/Debug`.

Cleanup runs on startup and every five minutes. It deletes completed files whose
ExpiresAt has passed, marks those jobs Expired, and notifies clients. Files that
cannot be deleted remain eligible for retry. Expired records remain in history.
Downloads are refused as soon as their expiration passes, even before the next sweep.

The database connection defaults to a file in the API working directory. Keep the
database and download folder when restarting the application. This startup recovery
assumes one API instance owns the database and queue.

Migration tooling is pinned in `dotnet-tools.json`. To create a future migration:

```powershell
dotnet tool restore
dotnet ef migrations add MigrationName --project src/YtDownloader.Infrastructure
```

SignalR is available at `/hubs/downloads`. Invoke `JoinJob(jobId)` to join the group
named after the GUID. The `JobProgress` event contains `jobId`, `status`, and
`progressPercent`; joining also sends a current snapshot. Invoke `LeaveJob(jobId)`
to leave. Group membership must be restored after reconnection.

Run Blazor with `dotnet run --project src/YtDownloader.Web --launch-profile https`.
Its development `Api:BaseUrl` is `http://localhost:5039/`, which works with either API
launch profile. HTTPS redirection is enabled outside Development. The base configuration
uses `https://localhost:7266/`. The download page uses
SignalR and falls back to HTTP status checks every five seconds while disconnected,
including initial connection failures. It rejoins after reconnecting and stops
monitoring terminal jobs or when the page is disposed.

History saves only the 20 most recent job IDs under `YtDownloader.jobIds` in this
browser's local storage. The API returns details only for the IDs supplied by that
browser. There are no user accounts; clearing browser storage clears its history.
Use Refresh history to reload statuses; job progress also updates the active entry.

API integration tests use WebApplicationFactory with the real mediator pipeline and
isolated SQLite databases, replacing YouTube and storage with substitutes. Worker tests
use a fake queue to check concurrency, cancellation, failures, duplicate jobs, and
scope disposal. Hub tests use SignalR over TestServer. They require neither
external network access nor FFmpeg.
Browser storage tests run with `node --test tests/YtDownloader.Web.Tests/jobHistory.test.mjs`.
