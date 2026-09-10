using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Shunshou.App;

public sealed class InputPathEventArgs(string? path) : EventArgs
{
    public string? Path { get; } = path;
}

public sealed class InputMoveEventArgs(string path, int offset) : EventArgs
{
    public string Path { get; } = path;
    public int Offset { get; } = offset;
}

/// <summary>A bounded, virtualized view of input files. Actions are requests; files are never changed here.</summary>
public sealed partial class InputFileList : UserControl
{
    private readonly ObservableCollection<InputFileItem> _items = [];
    private readonly Dictionary<Image, CancellationTokenSource> _requests = [];
    private SearchPreviewService? _previews;
    private CancellationTokenSource _viewCancellation = new();
    private bool _settingPaths;
    private bool _allowReorder;
    private bool _readOnly;

    public event EventHandler<InputPathEventArgs>? SelectionChanged;
    public event EventHandler<InputPathEventArgs>? RemoveRequested;
    public event EventHandler<InputMoveEventArgs>? MoveRequested;

    public string? SelectedPath => (Rows.SelectedItem as InputFileItem)?.FullPath;
    public IReadOnlyList<string> Paths => _items.Select(item => item.FullPath).ToArray();

    public bool AllowReorder
    {
        get => _allowReorder;
        set { if (_allowReorder != value) { _allowReorder = value; RefreshActions(); } }
    }

    public bool IsReadOnly
    {
        get => _readOnly;
        set
        {
            _readOnly = value;
            Rows.IsEnabled = !value;
            RefreshActions();
        }
    }

    public double MaximumListHeight
    {
        get => Rows.MaxHeight;
        set => Rows.MaxHeight = Math.Clamp(value, 72, 600);
    }

    public InputFileList()
    {
        InitializeComponent();
        Rows.ItemsSource = _items;
    }

    /// <summary>Preserves the current selected path where possible; first input is selected on an initially empty list.</summary>
    public void SetPaths(IEnumerable<string> paths, string? selectedPath = null)
    {
        string? previousSelection = SelectedPath;
        string[] normalized = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        selectedPath ??= previousSelection;
        _settingPaths = true;
        try
        {
            if (!_items.Select(item => item.FullPath).SequenceEqual(normalized, StringComparer.OrdinalIgnoreCase))
            {
                ResetRequests();
                var existing = _items.ToDictionary(item => item.FullPath, StringComparer.OrdinalIgnoreCase);
                _items.Clear();
                foreach (string path in normalized)
                    _items.Add(existing.TryGetValue(path, out var item) ? item : new InputFileItem(path));
            }
            RefreshActions();
            Rows.SelectedItem = _items.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.FullPath, selectedPath)) ?? _items.FirstOrDefault();
            Rows.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            FileCount.Text = _items.Count == 0 ? "还没有添加文件" : $"已添加 {_items.Count:N0} 项";
        }
        finally { _settingPaths = false; }
        if (!StringComparer.OrdinalIgnoreCase.Equals(previousSelection, SelectedPath))
        {
            string? selected = SelectedPath;
            if (selected != null)
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (StringComparer.OrdinalIgnoreCase.Equals(SelectedPath, selected) && Rows.SelectedItem != null)
                    {
                        Rows.UpdateLayout();
                        Rows.ScrollIntoView(Rows.SelectedItem);
                    }
                });
            SelectionChanged?.Invoke(this, new InputPathEventArgs(SelectedPath));
        }
    }

    public void SelectPath(string? path)
    {
        Rows.SelectedItem = path == null ? null : _items.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.FullPath, path));
        if (Rows.SelectedItem != null) Rows.ScrollIntoView(Rows.SelectedItem);
    }

    private void RefreshActions()
    {
        for (int i = 0; i < _items.Count; i++) _items[i].ConfigureActions(i, _items.Count, _allowReorder, _readOnly);
    }

    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_settingPaths) SelectionChanged?.Invoke(this, new InputPathEventArgs(SelectedPath));
    }

    private void Remove_Click(object sender, RoutedEventArgs args)
    {
        if (!_readOnly && sender is FrameworkElement { DataContext: InputFileItem item })
            RemoveRequested?.Invoke(this, new InputPathEventArgs(item.FullPath));
    }

    private void Move_Click(object sender, RoutedEventArgs args)
    {
        if (_readOnly || !_allowReorder || sender is not Button { DataContext: InputFileItem item } button) return;
        int offset = button.Tag as string == "-1" ? -1 : 1;
        int index = _items.IndexOf(item);
        if (index >= 0 && index + offset >= 0 && index + offset < _items.Count)
            MoveRequested?.Invoke(this, new InputMoveEventArgs(item.FullPath, offset));
    }

    private void Control_Loaded(object sender, RoutedEventArgs args) => _previews ??= new SearchPreviewService(DispatcherQueue);

    private void Control_Unloaded(object sender, RoutedEventArgs args)
    {
        ResetRequests();
        _previews?.Dispose();
        _previews = null;
    }

    private void ResetRequests()
    {
        _viewCancellation.Cancel();
        _viewCancellation.Dispose();
        _viewCancellation = new CancellationTokenSource();
        foreach (var request in _requests.Values.ToArray()) request.Cancel();
        foreach (var item in _items) item.Thumbnail = null;
    }

    private async void Thumbnail_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Image image) return;
        if (image.Tag is InputFileItem previousItem && !ReferenceEquals(previousItem, image.DataContext)) ReleasePreview(image);
        if (image.DataContext is not InputFileItem item) return;
        _previews ??= new SearchPreviewService(DispatcherQueue);
        image.Tag = item;
        if (_requests.Remove(image, out var previous)) previous.Cancel();
        if (item.Thumbnail != null && item.HasMetadata) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_viewCancellation.Token);
        _requests[image] = cancellation;
        try
        {
            if (!item.HasMetadata)
            {
                var detail = await Task.Run(() => InputFileItem.ReadMetadata(item.FullPath), cancellation.Token);
                if (!cancellation.IsCancellationRequested && ReferenceEquals(image.DataContext, item)) item.SetMetadata(detail);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var thumbnail = await _previews.LoadAsync(item.FullPath, 96, cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(image.DataContext, item)) item.Thumbnail = thumbnail;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.LogException(ex, "input-thumbnail"); }
        finally
        {
            if (_requests.TryGetValue(image, out var current) && ReferenceEquals(current, cancellation)) _requests.Remove(image);
            cancellation.Dispose();
        }
    }

    private void Thumbnail_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is Image image) ReleasePreview(image);
    }

    private void Thumbnail_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is not Image image) return;
        ReleasePreview(image);
        if (image.IsLoaded) Thumbnail_Loaded(image, new RoutedEventArgs());
    }

    private void ReleasePreview(Image image)
    {
        if (_requests.Remove(image, out var cancellation)) cancellation.Cancel();
        if (image.Tag is InputFileItem item) item.Thumbnail = null;
        image.Tag = null;
    }
}

public sealed class InputFileItem(string fullPath) : INotifyPropertyChanged
{
    public string FullPath { get; } = fullPath;
    public string Name { get; } = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath)) is { Length: > 0 } name ? name : fullPath;
    public string RemoveLabel => $"从列表移除 {Name}";
    public string MoveUpLabel => $"上移 {Name}";
    public string MoveDownLabel => $"下移 {Name}";
    public bool HasMetadata { get; private set; }
    public string Detail { get; private set; } = "读取文件信息…";
    public string Glyph { get; private set; } = "\uE8A5";
    public bool CanRemove { get; private set; } = true;
    public bool CanMoveUp { get; private set; }
    public bool CanMoveDown { get; private set; }
    public Visibility ReorderVisibility { get; private set; } = Visibility.Collapsed;
    public Visibility IconVisibility => Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;
    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; Changed(); Changed(nameof(IconVisibility)); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void ConfigureActions(int index, int count, bool reorder, bool readOnly)
    {
        CanRemove = !readOnly;
        CanMoveUp = reorder && !readOnly && index > 0;
        CanMoveDown = reorder && !readOnly && index + 1 < count;
        ReorderVisibility = reorder ? Visibility.Visible : Visibility.Collapsed;
        Changed(nameof(CanRemove)); Changed(nameof(CanMoveUp)); Changed(nameof(CanMoveDown)); Changed(nameof(ReorderVisibility));
    }

    internal readonly record struct Metadata(string Detail, bool Folder);

    internal static Metadata ReadMetadata(string path)
    {
        try
        {
            bool folder = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            if (folder) return new("文件夹", true);
            long bytes = new FileInfo(path).Length;
            string size = bytes < 1000 ? $"{bytes:N0} 字节" : bytes < 1_000_000 ? $"{bytes / 1000d:0.#} KB" : bytes < 1_000_000_000 ? $"{bytes / 1_000_000d:0.##} MB" : $"{bytes / 1_000_000_000d:0.##} GB";
            string extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            return new((extension.Length == 0 ? "文件" : extension) + " · " + size, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new("文件已移动或无法访问", false);
        }
    }

    internal void SetMetadata(Metadata metadata)
    {
        HasMetadata = true;
        Detail = metadata.Detail;
        Glyph = metadata.Folder ? "\uE8B7" : "\uE8A5";
        Changed(nameof(Detail)); Changed(nameof(Glyph));
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
