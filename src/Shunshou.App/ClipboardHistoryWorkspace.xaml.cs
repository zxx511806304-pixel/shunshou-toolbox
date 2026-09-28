using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Shunshou.App;

/// <summary>Session-only clipboard history: text / image / file records live in memory, cleared when the window closes, nothing is uploaded.</summary>
public sealed partial class ClipboardHistoryWorkspace : UserControl
{
    private const int MaxTextEntries = 50;
    private const int MaxImageEntries = 10;
    private const int MaxFileEntries = 30;
    private const int MaxThumbnailWidth = 96; // Decode width for both thumbnail display and pixel fingerprint.
    private const int TextPreviewLength = 60;

    private readonly ObservableCollection<ClipboardEntry> _rows = new();
    private bool _paused;

    public ClipboardHistoryWorkspace()
    {
        InitializeComponent();
        History.ItemsSource = _rows;
        // Wire up the DataTemplateSelector in code-behind; declaring <local:ClipboardEntryTemplateSelector>
        // as a XAML resource forces the XAML compiler to resolve the local type at Pass1, which fails on
        // clean builds because the intermediate assembly has not been produced yet.
        var selector = new ClipboardEntryTemplateSelector
        {
            TextTemplate = (DataTemplate)Resources["TextRowTemplate"],
            ImageTemplate = (DataTemplate)Resources["ImageRowTemplate"],
            FilesTemplate = (DataTemplate)Resources["FilesRowTemplate"]
        };
        History.ItemTemplateSelector = selector;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public nint HostWindowHandle { get; set; }

    private void OnLoaded(object sender, RoutedEventArgs e) => Clipboard.ContentChanged += Clipboard_ContentChanged;

    private void OnUnloaded(object sender, RoutedEventArgs e) => Clipboard.ContentChanged -= Clipboard_ContentChanged;

    private void Clipboard_ContentChanged(object? sender, object args)
    {
        if (_paused) return;
        // The event can arrive off the UI thread; marshal before touching the collection / bitmaps.
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_paused) return;
            try
            {
                var content = Clipboard.GetContent();
                if (content is null) return;

                if (content.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await content.GetStorageItemsAsync();
                    RecordFiles(items);
                    return;
                }

                if (content.Contains(StandardDataFormats.Bitmap))
                {
                    var bitmapRef = await content.GetBitmapAsync();
                    await RecordImageAsync(bitmapRef);
                    return;
                }

                if (content.Contains(StandardDataFormats.Text))
                {
                    var text = await content.GetTextAsync();
                    if (!string.IsNullOrWhiteSpace(text)) AddOrPromoteText(text.Trim());
                    return;
                }
            }
            catch (Exception)
            {
                ShowNotice("系统暂时无法读取剪贴板内容，请稍后再试。");
            }
        });
    }

    private void AddOrPromoteText(string text)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Kind == ClipboardEntryKind.Text && string.Equals(_rows[i].FullText, text, StringComparison.Ordinal))
            {
                _rows.RemoveAt(i);
                break;
            }
        }
        _rows.Insert(0, ClipboardEntry.ForText(DateTime.Now, text));
        EnforceLimit(ClipboardEntryKind.Text, MaxTextEntries);
        EmptyLabel.Visibility = Visibility.Collapsed;
    }

    private void RecordFiles(IReadOnlyList<IStorageItem> items)
    {
        if (items is null || items.Count == 0) return;
        var paths = items.OfType<IStorageItem>().Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count == 0) return;

        // Dedupe by path-set equality.
        var newSet = paths.OrderBy(p => p, StringComparer.Ordinal).ToList();
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Kind != ClipboardEntryKind.Files || _rows[i].FilePathList is null) continue;
            var existing = _rows[i].FilePathList!.OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (existing.SequenceEqual(newSet, StringComparer.Ordinal))
            {
                _rows.RemoveAt(i);
                break;
            }
        }

        _rows.Insert(0, ClipboardEntry.ForFiles(DateTime.Now, paths));
        EnforceLimit(ClipboardEntryKind.Files, MaxFileEntries);
        EmptyLabel.Visibility = Visibility.Collapsed;
    }

    private async Task RecordImageAsync(RandomAccessStreamReference bitmapRef)
    {
        if (bitmapRef is null) return;
        byte[] bytes;
        using (var stream = await bitmapRef.OpenReadAsync())
        {
            using var reader = new DataReader(stream)
            {
                ByteOrder = ByteOrder.LittleEndian
            };
            var size = (uint)stream.Size;
            await reader.LoadAsync(size);
            bytes = new byte[size];
            reader.ReadBytes(bytes);
        }
        if (bytes.Length == 0) return;

        // Decode once into a small bitmap: it drives both the thumbnail and the pixel fingerprint.
        // Deduping on decoded pixels (not on transport bytes) is required because the clipboard can
        // hand back the same image as different encodings on each read (e.g. PNG vs CF_DIB).
        var (source, fingerprint) = await DecodeImageAsync(bytes);

        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Kind == ClipboardEntryKind.Image && string.Equals(_rows[i].Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                _rows.RemoveAt(i);
                break;
            }
        }

        _rows.Insert(0, ClipboardEntry.ForImage(DateTime.Now, bytes, source, fingerprint));
        EnforceLimit(ClipboardEntryKind.Image, MaxImageEntries);
        EmptyLabel.Visibility = Visibility.Collapsed;
    }

    private static async Task<(SoftwareBitmapSource? Source, string Fingerprint)> DecodeImageAsync(byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);

            uint targetWidth = Math.Min(MaxThumbnailWidth, decoder.PixelWidth);
            uint targetHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * (targetWidth / (double)decoder.PixelWidth)));
            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                ScaledWidth = targetWidth,
                ScaledHeight = targetHeight,
                InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
            };
            var bmp = await decoder.GetSoftwareBitmapAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);

            var pixels = new byte[4 * bmp.PixelWidth * bmp.PixelHeight];
            bmp.CopyToBuffer(pixels.AsBuffer());
            var hash = Convert.ToHexString(SHA256.HashData(pixels));
            var fingerprint = $"px:{bmp.PixelWidth}x{bmp.PixelHeight}:{hash}";

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bmp);
            return (source, fingerprint);
        }
        catch (Exception)
        {
            // Broken payload — still keep the entry so the user has the raw bytes for re-paste;
            // fall back to a transport-byte signature (only matches identical encodings).
            return (null, "bytes:" + ComputeByteSignature(bytes));
        }
    }

    private static long ComputeByteSignature(byte[] bytes)
    {
        var sample = 0L;
        var take = Math.Min(4096, bytes.Length);
        for (var i = 0; i < take; i += 64)
        {
            sample = sample * 31 + bytes[i];
        }
        return ((long)bytes.Length << 32) ^ sample;
    }

    private static string BuildFilesPreview(IReadOnlyList<string> paths)
    {
        var names = paths.Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).ToList();
        var head = string.Join(", ", names.Take(3));
        return names.Count <= 3 ? $"{names.Count} 个文件：{head}" : $"{names.Count} 个文件：{head} 等";
    }

    private static string FormatByteSize(int bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024):F1} MB"
    };

    private void EnforceLimit(ClipboardEntryKind kind, int max)
    {
        var countOfKind = 0;
        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            if (_rows[i].Kind == kind)
            {
                countOfKind++;
                if (countOfKind > max) _rows.RemoveAt(i);
            }
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _paused = PauseToggle.IsChecked == true;
        PauseToggle.Content = _paused ? "继续记录" : "暂停记录";
        StatusText.Text = _paused ? "已暂停记录" : "";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _rows.Clear();
        EmptyLabel.Visibility = Visibility.Visible;
        StatusText.Text = "已清空";
    }

    private async void CopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ClipboardEntry entry }) return;
        try
        {
            switch (entry.Kind)
            {
                case ClipboardEntryKind.Text:
                    await CopyTextAsync(entry.FullText ?? string.Empty);
                    AddOrPromoteText(entry.FullText ?? string.Empty);
                    StatusText.Text = "已复制";
                    break;
                case ClipboardEntryKind.Image:
                    await CopyImageAsync(entry.ImageBytes);
                    StatusText.Text = "已复制图片";
                    break;
                case ClipboardEntryKind.Files:
                    var ok = await CopyFilesAsync(entry.FilePathList);
                    StatusText.Text = ok ? "已复制文件" : "部分文件已不存在";
                    if (!ok) ShowNotice("这些文件已不存在或无法访问，已跳过缺失项。");
                    break;
            }
        }
        catch (Exception)
        {
            ShowNotice("系统暂时拒绝了剪贴板写入，请稍后再试。");
        }
    }

    private static async Task CopyTextAsync(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        await Task.CompletedTask;
    }

    private static async Task CopyImageAsync(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return;
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var package = new DataPackage();
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private static async Task<bool> CopyFilesAsync(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) return false;
        var storageItems = new List<IStorageItem>();
        foreach (var path in paths)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                storageItems.Add(file);
            }
            catch (Exception)
            {
                // File removed or inaccessible; skip quietly.
            }
        }
        if (storageItems.Count == 0) return false;
        var package = new DataPackage();
        package.SetStorageItems(storageItems);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        return storageItems.Count == paths.Count;
    }

    private void ShowNotice(string message)
    {
        Notice.Title = "剪贴板";
        Notice.Message = message;
        Notice.Severity = InfoBarSeverity.Warning;
        Notice.IsOpen = true;
    }
}

public enum ClipboardEntryKind { Text, Image, Files }

public sealed class ClipboardEntry
{
    private ClipboardEntry(ClipboardEntryKind kind, string timeText, string previewText, string? fullText, byte[]? imageBytes, SoftwareBitmapSource? imageSource, IReadOnlyList<string>? filePathList, string? fingerprint)
    {
        Kind = kind;
        TimeText = timeText;
        PreviewText = previewText;
        FullText = fullText;
        ImageBytes = imageBytes;
        ImageSource = imageSource;
        FilePathList = filePathList;
        Fingerprint = fingerprint;
    }

    public static ClipboardEntry ForText(DateTime time, string text)
    {
        var singleLine = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        var preview = singleLine.Length <= TextPreviewLength ? singleLine : singleLine[..TextPreviewLength] + "…";
        return new ClipboardEntry(ClipboardEntryKind.Text, time.ToString("HH:mm:ss"), preview, text, null, null, null, null);
    }

    public static ClipboardEntry ForImage(DateTime time, byte[] bytes, SoftwareBitmapSource? source, string fingerprint)
    {
        return new ClipboardEntry(ClipboardEntryKind.Image, time.ToString("HH:mm:ss"), $"图片 · {FormatByteSize(bytes.Length)}", string.Empty, bytes, source, null, fingerprint);
    }

    public static ClipboardEntry ForFiles(DateTime time, IReadOnlyList<string> paths)
    {
        return new ClipboardEntry(ClipboardEntryKind.Files, time.ToString("HH:mm:ss"), BuildFilesPreview(paths), string.Empty, null, null, paths, null);
    }

    public ClipboardEntryKind Kind { get; }
    public string TimeText { get; }
    public string PreviewText { get; }
    public string? FullText { get; }
    public byte[]? ImageBytes { get; }
    public SoftwareBitmapSource? ImageSource { get; }
    public IReadOnlyList<string>? FilePathList { get; }
    public string? Fingerprint { get; }

    private const int TextPreviewLength = 60;

    private static string BuildFilesPreview(IReadOnlyList<string> paths)
    {
        var names = paths.Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).ToList();
        var head = string.Join(", ", names.Take(3));
        return names.Count <= 3 ? $"{names.Count} 个文件：{head}" : $"{names.Count} 个文件：{head} 等";
    }

    private static string FormatByteSize(int bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024):F1} MB"
    };
}

public sealed class ClipboardEntryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? ImageTemplate { get; set; }
    public DataTemplate? FilesTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is ClipboardEntry e)
        {
            return e.Kind switch
            {
                ClipboardEntryKind.Image => ImageTemplate,
                ClipboardEntryKind.Files => FilesTemplate,
                _ => TextTemplate
            };
        }
        return base.SelectTemplateCore(item);
    }
}
