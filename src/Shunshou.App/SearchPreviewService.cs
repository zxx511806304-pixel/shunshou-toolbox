using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using FileAttributes = System.IO.FileAttributes;

namespace Shunshou.App;

/// <summary>Local, bounded thumbnails. A null result is an ordinary file-icon fallback.</summary>
internal sealed class SearchPreviewService : IDisposable
{
    private const int MaximumEntries = 128;
    private const int MaximumCacheBytes = 16 * 1024 * 1024;
    private const int MaximumThumbnailBytes = 2 * 1024 * 1024;
    private const long MaximumFallbackPixels = 4_000_000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".heic", ".heif", ".avif" };
    private static readonly HashSet<string> ShortcutExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".lnk", ".url", ".website", ".library-ms", ".search-ms", ".appref-ms" };
    // Recall flags are not exposed by every System.IO.FileAttributes version.
    private const uint UnavailableAttributes = 0x00001000 | 0x00040000 | 0x00400000;
    private readonly DispatcherQueue _dispatcher;
    private readonly SemaphoreSlim _workers = new(3);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly object _cacheLock = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _cache = new();
    private readonly LinkedList<CacheEntry> _recent = new();
    private int _cacheBytes;
    private bool _disposed;

    public SearchPreviewService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _lifetimeToken = _lifetime.Token;
    }

    /// <summary>
    /// Request 96–128 pixels for a result row and 480 for the selected item.
    /// Caller cancellation propagates; timeout/unavailable previews return null.
    /// Returned BitmapImages belong to the supplied UI dispatcher.
    /// </summary>
    public async Task<BitmapImage?> LoadAsync(string fullPath, int requestedSize,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_cacheLock) if (_disposed) return null;
        int size = Math.Clamp(requestedSize, 64, 640);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        deadline.CancelAfter(RequestTimeout);
        var deadlineToken = deadline.Token;
        // WaitAsync stops the caller waiting, not the worker. An outstanding native
        // shell request retains its slot until it really finishes, even after timeout.
        var work = Task.Run(() => LoadCoreAsync(fullPath, size, deadlineToken), CancellationToken.None);
        try
        {
            return await work.WaitAsync(deadlineToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            // Observe a late failure when the caller stopped awaiting the worker.
            _ = work.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task<BitmapImage?> LoadCoreAsync(string path, int size, CancellationToken ct)
    {
        await _workers.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var identity = GetLocalIdentity(path, size);
            if (identity is null) return null;
            var (key, isDirectory) = identity.Value;
            if (!TryGetCached(key, out byte[]? encoded))
            {
                encoded = await ReadThumbnailAsync(key.Path, isDirectory, size, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (encoded is null) return null;
                AddCached(key, encoded);
            }
            ct.ThrowIfCancellationRequested();
            return await CreateBitmapAsync(encoded!, size, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Removed files, unsupported codecs and shell thumbnail failures should
            // never interrupt file search or show a modal error for each result.
            return null;
        }
        finally { _workers.Release(); }
    }

    private static (CacheKey Key, bool IsDirectory)? GetLocalIdentity(string path, int size)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        path = Path.GetFullPath(path);
        if (ShortcutExtensions.Contains(Path.GetExtension(path))) return null;
        // Only ordinary drive-letter paths are accepted; no UNC/device/URL paths.
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return null;
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram)) return null;
        var attributes = File.GetAttributes(path);
        if (((uint)attributes & UnavailableAttributes) != 0) return null;
        // Check parents too: a local-looking junction can lead to a network share.
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            var flags = File.GetAttributes(current);
            if ((flags & FileAttributes.ReparsePoint) != 0 || ((uint)flags & UnavailableAttributes) != 0) return null;
        }
        bool folder = (attributes & FileAttributes.Directory) != 0;
        long length = folder ? 0 : new FileInfo(path).Length;
        return (new CacheKey(path, File.GetLastWriteTimeUtc(path).Ticks, length, size), folder);
    }

    private static async Task<byte[]?> ReadThumbnailAsync(string path, bool folder, int size, CancellationToken ct)
    {
        StorageItemThumbnail? thumbnail = null;
        bool image = !folder && ImageExtensions.Contains(Path.GetExtension(path));
        var options = ThumbnailOptions.ResizeThumbnail;
        // Existing document thumbnails/icons are enough for search; do not ask a
        // document's registered application to generate a fresh preview.
        if (!image) options |= ThumbnailOptions.ReturnOnlyIfCached;
        try
        {
            // ResizeThumbnail asks for fixed physical pixels, not display-scale growth.
            // Native operations deliberately receive no cancellation token: a cancelled
            // caller cannot release a concurrency slot while the shell is still busy.
            if (folder)
            {
                var item = await StorageFolder.GetFolderFromPathAsync(path);
                ct.ThrowIfCancellationRequested();
                thumbnail = await item.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)size, options);
            }
            else
            {
                var item = await StorageFile.GetFileFromPathAsync(path);
                ct.ThrowIfCancellationRequested();
                thumbnail = await item.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)size, options);
            }
            ct.ThrowIfCancellationRequested();
            if (thumbnail is not null && thumbnail.Size is > 0 and <= MaximumThumbnailBytes &&
                (!image || thumbnail.Type == ThumbnailType.Image))
            {
                var bytes = new byte[(int)thumbnail.Size];
                using var stream = thumbnail.AsStreamForRead();
                await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
                return bytes;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally { thumbnail?.Dispose(); }
        ct.ThrowIfCancellationRequested();
        return folder ? null : ReadImageFallback(path, size, ct);
    }

    private static byte[]? ReadImageFallback(string path, int size, CancellationToken ct)
    {
        if (!ImageExtensions.Contains(Path.GetExtension(path))) return null;
        // Bundled Skia supplies WebP even on machines with no Windows WebP extension.
        // Ask the codec for a native scaled decode; never allocate an original-size
        // giant bitmap if that codec cannot decode down to a bounded working buffer.
        using var codec = SKCodec.Create(path);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0) return null;
        var dimensions = codec.GetScaledDimensions(Math.Min(1f, (float)size / Math.Max(codec.Info.Width, codec.Info.Height)));
        if (dimensions.Width <= 0 || dimensions.Height <= 0 || (long)dimensions.Width * dimensions.Height > MaximumFallbackPixels) return null;
        ct.ThrowIfCancellationRequested();
        var info = new SKImageInfo(dimensions.Width, dimensions.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success) return null;
        ct.ThrowIfCancellationRequested();
        int orientation = (int)codec.EncodedOrigin;
        bool swap = orientation is >= 5 and <= 8;
        int displayWidth = swap ? info.Height : info.Width, displayHeight = swap ? info.Width : info.Height;
        double scale = Math.Min(1d, (double)size / Math.Max(displayWidth, displayHeight));
        int width = Math.Max(1, (int)Math.Round(displayWidth * scale));
        int height = Math.Max(1, (int)Math.Round(displayHeight * scale));
        using var output = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)width / displayWidth, (float)height / displayHeight);
            var transform = orientation switch
            {
                2 => new SKMatrix(-1, 0, info.Width, 0, 1, 0, 0, 0, 1),
                3 => new SKMatrix(-1, 0, info.Width, 0, -1, info.Height, 0, 0, 1),
                4 => new SKMatrix(1, 0, 0, 0, -1, info.Height, 0, 0, 1),
                5 => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
                6 => new SKMatrix(0, -1, info.Height, 1, 0, 0, 0, 0, 1),
                7 => new SKMatrix(0, -1, info.Height, -1, 0, info.Width, 0, 0, 1),
                8 => new SKMatrix(0, 1, 0, -1, 0, info.Width, 0, 0, 1),
                _ => SKMatrix.Identity
            };
            canvas.Concat(transform);
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        }
        ct.ThrowIfCancellationRequested();
        using var encoded = output.Encode(SKEncodedImageFormat.Png, 100);
        return encoded is not null && encoded.Size <= MaximumThumbnailBytes ? encoded.ToArray() : null;
    }

    private Task<BitmapImage?> CreateBitmapAsync(byte[] encoded, int size, CancellationToken ct)
    {
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0) return Task.FromResult<BitmapImage?>(null);
        double scale = Math.Min(1d, (double)size / Math.Max(codec.Info.Width, codec.Info.Height));
        int width = Math.Max(1, (int)Math.Round(codec.Info.Width * scale));
        int height = Math.Max(1, (int)Math.Round(codec.Info.Height * scale));
        var completion = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(encoded);
                    await writer.StoreAsync();
                }
                stream.Seek(0);
                var image = new BitmapImage { DecodePixelType = DecodePixelType.Physical,
                    DecodePixelWidth = width, DecodePixelHeight = height };
                await image.SetSourceAsync(stream);
                ct.ThrowIfCancellationRequested();
                completion.TrySetResult(image);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetResult(null);
        return completion.Task;
    }

    private bool TryGetCached(CacheKey key, out byte[]? bytes)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                bytes = node.Value.Bytes;
                return true;
            }
        }
        bytes = null;
        return false;
    }

    private void AddCached(CacheKey key, byte[] bytes)
    {
        lock (_cacheLock)
        {
            if (_disposed || _cache.ContainsKey(key)) return;
            while (_recent.Count > 0 && (_cache.Count >= MaximumEntries || _cacheBytes + bytes.Length > MaximumCacheBytes))
            {
                var oldest = _recent.Last!;
                _recent.RemoveLast();
                _cache.Remove(oldest.Value.Key);
                _cacheBytes -= oldest.Value.Bytes.Length;
            }
            _cache.Add(key, _recent.AddFirst(new CacheEntry(key, bytes)));
            _cacheBytes += bytes.Length;
        }
    }

    public void Dispose()
    {
        lock (_cacheLock)
        {
            if (_disposed) return;
            _disposed = true;
            _cache.Clear();
            _recent.Clear();
            _cacheBytes = 0;
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
        // The managed-only semaphore stays alive for any late native completions.
    }

    private readonly record struct CacheKey(string Path, long Modified, long Length, int Size);
    private sealed record CacheEntry(CacheKey Key, byte[] Bytes);
}
