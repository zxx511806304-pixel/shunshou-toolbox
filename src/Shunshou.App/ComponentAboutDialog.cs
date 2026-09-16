using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Shunshou.Core;

namespace Shunshou.App;

internal sealed class ComponentEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Version { get; set; } = "";
    public string License { get; set; } = "";
    public string ProjectUrl { get; set; } = "";
    public string AdditionalUrl { get; set; } = "";
}

internal sealed class ComponentCatalog
{
    public int SchemaVersion { get; set; }
    public List<ComponentEntry> Components { get; set; } = [];
    public List<ComponentEntry> References { get; set; } = [];
    public List<ComponentEntry> Dependencies { get; set; } = [];

    internal static ComponentCatalog Load()
    {
        var file = FindDocument("components.json") ?? Path.Combine(AppContext.BaseDirectory, "Assets", "ComponentSources.json");
        if (!File.Exists(file) || new FileInfo(file).Length > 2_000_000) throw new InvalidDataException("组件来源清单不可用。");
        var catalog = JsonSerializer.Deserialize<ComponentCatalog>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("组件来源清单无法读取。");
        if (catalog.SchemaVersion != 1) throw new InvalidDataException("组件来源清单版本不匹配。");
        foreach (var entry in catalog.Components)
        {
            if (entry.Id == "dotnet") entry.Version = Environment.Version.ToString();
            else if (string.IsNullOrWhiteSpace(entry.Version)) entry.Version = "见发布包中的组件清单";
        }
        return catalog;
    }

    internal static string? FindDocument(string name)
    {
        if (Path.GetFileName(name) != name) return null;
        var installed = Path.Combine(AppPaths.InstallationDirectory, "docs", name);
        if (File.Exists(installed)) return installed;
        // Development builds use repository notices; installed apps resolve above.
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (!Directory.Exists(Path.Combine(folder.FullName, "src", "Shunshou.App"))) continue;
            var candidate = name == "THIRD-PARTY-NOTICES.md" ? Path.Combine(folder.FullName, name) : Path.Combine(folder.FullName, "docs", name);
            return File.Exists(candidate) ? candidate : null;
        }
        return null;
    }
}

internal static class ComponentAboutDialog
{
    internal static async Task ShowAsync(XamlRoot xamlRoot)
    {
        var dialog = Create(xamlRoot);
        await dialog.ShowAsync();
    }

    internal static ContentDialog Create(XamlRoot xamlRoot)
    {
        var version = typeof(App).Assembly.GetName().Version;
        var content = new StackPanel { Spacing = 14, MinWidth = 420, MaxWidth = 620 };
        content.Children.Add(Muted($"版本 {version?.Major}.{version?.Minor}.{version?.Build}"));
        content.Children.Add(new TextBlock { Text = "开源组件与来源", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        var status = new InfoBar { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Error };
        try
        {
            var catalog = ComponentCatalog.Load();
            foreach (var group in catalog.Components.GroupBy(item => item.Group))
                content.Children.Add(Group(group.Key, group, status));
            if (catalog.Dependencies.Count > 0)
                content.Children.Add(Group($"NuGet 依赖清单（{catalog.Dependencies.Count}）", catalog.Dependencies, status,
                    "来自本次构建的 NuGet 依赖记录；包括构建依赖及其他平台包，不代表这些产品的全部功能已集成。"));
            if (catalog.References.Count > 0)
                content.Children.Add(Group("设计与研究参考", catalog.References, status));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            status.Title = "无法读取组件清单"; status.Message = ex.Message; status.IsOpen = true;
        }
        var notices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (label, file) in new[] { ("第三方声明", "THIRD-PARTY-NOTICES.md"), ("恢复组件源码说明", "RecoverySources.md") })
        {
            var path = ComponentCatalog.FindDocument(file);
            if (path is null) continue;
            var button = new Button { Content = label };
            button.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception ex) { status.Title = "无法打开声明文件"; status.Message = ex.Message; status.IsOpen = true; }
            };
            notices.Children.Add(button);
        }
        content.Children.Add(notices);
        content.Children.Add(status);
        var dialog = new ContentDialog
        {
            Title = "关于顺手工具箱", XamlRoot = xamlRoot, CloseButtonText = "关闭",
            RequestedTheme = (xamlRoot.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
            Content = new ScrollViewer { Content = content, MaxHeight = Math.Max(220, Math.Min(510, xamlRoot.Size.Height - 215)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///ToolboxTheme.xaml") });
        return dialog;
    }

    private static Expander Group(string title, IEnumerable<ComponentEntry> entries, InfoBar status, string? introduction = null)
    {
        var panel = new StackPanel { Spacing = 18, Padding = new Thickness(0, 4, 0, 8) };
        if (introduction is not null) panel.Children.Add(Muted(introduction));
        foreach (var entry in entries)
        {
            var item = new StackPanel { Spacing = 4 };
            item.Children.Add(new TextBlock { Text = entry.Name, FontWeight = Microsoft.UI.Text.FontWeights.Medium, TextWrapping = TextWrapping.Wrap });
            item.Children.Add(Muted(entry.Purpose));
            item.Children.Add(Muted($"{entry.Version} · {entry.License}"));
            var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            AddLink(links, "项目来源", entry.ProjectUrl, entry.Name, status);
            AddLink(links, "相关链接", entry.AdditionalUrl, entry.Name, status);
            item.Children.Add(links);
            panel.Children.Add(item);
        }
        return new Expander { Header = title, Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    private static TextBlock Muted(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["MutedTextStyle"]
    };

    private static void AddLink(Panel panel, string label, string url, string component, InfoBar status)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)) return;
        var button = (HyperlinkButton)XamlReader.Load("<HyperlinkButton xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Padding=\"0,3\" Foreground=\"{ThemeResource BrandBrush}\" />");
        button.Content = label;
        AutomationProperties.SetName(button, component + "，" + label);
        ToolTipService.SetToolTip(button, uri.AbsoluteUri);
        button.Click += async (_, _) =>
        {
            try { if (!await Windows.System.Launcher.LaunchUriAsync(uri)) throw new IOException("请检查默认浏览器设置。"); }
            catch (Exception ex) { status.Title = "无法打开项目来源"; status.Message = ex.Message; status.IsOpen = true; }
        };
        panel.Children.Add(button);
    }
}
