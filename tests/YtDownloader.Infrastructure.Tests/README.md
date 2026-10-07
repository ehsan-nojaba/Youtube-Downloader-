# Infrastructure tests

Normal `dotnet test` runs local tests and skips the live YouTube integration test.
To opt in from PowerShell:

```powershell
$env:YTDOWNLOADER_RUN_INTEGRATION = "1"
dotnet test tests/YtDownloader.Infrastructure.Tests --filter "Category=Integration"
Remove-Item Env:YTDOWNLOADER_RUN_INTEGRATION
```

The integration test downloads the audio of the short Big Buck Bunny loves Creative Commons
([video](https://www.youtube.com/watch?v=4-Ddumty4mk)),
by Renderfarm.fi and Studio Lumikuu, licensed under
[CC BY 3.0](https://resources.creativecommons.org/big-buck-bunny-loves-creative-commons/).
It needs internet access, but does not need FFmpeg. Temporary files are deleted.

FFmpeg must be installed separately for conversions. Configure `Ffmpeg:Path`,
`FileStorage:RootPath`, and `FileStorage:TempPath` in API appsettings.
`AddInfrastructure(configuration)` binds these sections and registers the adapters,
SQLite EF repository, unit of work, and bounded process-local queue. The API's hosted
worker processes jobs and publishes progress through the SignalR download hub.
