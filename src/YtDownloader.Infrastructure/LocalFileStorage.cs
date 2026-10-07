using Microsoft.Extensions.Options;
using YtDownloader.Application;
using YtDownloader.Domain;

namespace YtDownloader.Infrastructure;

public sealed class LocalFileStorage : IFileStorage
{
    private readonly string root;
    private readonly string tempRoot;

    public LocalFileStorage(IOptions<LocalFileStorageOptions> options)
    {
        root = Path.GetFullPath(options.Value.RootPath);
        tempRoot = Path.GetFullPath(options.Value.TempPath);
    }

    public string GetOutputPath(Guid jobId, MediaFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(jobId, Guid.Empty);
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format));
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"{jobId:N}.{(format == MediaFormat.Mp4 ? "mp4" : "mp3")}");
    }

    public async Task<Result<string>> SaveAsync(string fileName, Stream source, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, $"{Guid.NewGuid():N}-{SanitizeFileName(fileName)}");
        var complete = false;
        try
        {
            Directory.CreateDirectory(root);
            await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
                await source.CopyToAsync(destination, cancellationToken);
            complete = true;
            return Result<string>.Success(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Failure(new("StorageUnavailable", "The media file could not be saved."));
        }
        finally
        {
            if (!complete && File.Exists(path))
                File.Delete(path);
        }
    }

    public Task<Result<DownloadFileDto>> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWithin(path, root))
            return Task.FromResult(Result<DownloadFileDto>.Failure(new("InvalidPath", "The file path is outside storage.")));
        if (!File.Exists(path))
            return Task.FromResult(Result<DownloadFileDto>.Failure(new("FileNotFound", "The media file was not found.")));
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            return Task.FromResult(Result<DownloadFileDto>.Success(new(stream, Path.GetFileName(path),
                Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg" : "video/mp4")));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Result<DownloadFileDto>.Failure(new("StorageUnavailable", "The media file could not be opened.")));
        }
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureManagedPath(path);
        File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult((IsWithin(path, root) || IsWithin(path, tempRoot)) && File.Exists(path));
    }

    public static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        var name = new string(fileName.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray())
            .Trim(' ', '.');
        return string.IsNullOrWhiteSpace(name) ? "download" : name[..Math.Min(name.Length, 180)];
    }

    private void EnsureManagedPath(string path)
    {
        if (!IsWithin(path, root) && !IsWithin(path, tempRoot))
            throw new ArgumentException("The path must be inside storage or the media temporary folder.", nameof(path));
    }

    private static bool IsWithin(string path, string directory)
    {
        var resolved = Path.GetFullPath(path);
        return resolved.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
