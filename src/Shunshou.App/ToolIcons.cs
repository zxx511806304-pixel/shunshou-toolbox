using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;

namespace Shunshou.App;

/// <summary>Theme-aware, original rounded outline icons; no system glyph dependency.</summary>
public static class ToolIcons
{
    public static IconElement Create(string name, double size = 20)
    {
        var data = ToolIconData.Paths.TryGetValue(name, out var path) ? path : ToolIconData.Paths["pdf"];
        var icon = (PathIcon)XamlReader.Load($"<PathIcon xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"{data}\" />");
        icon.Width = size;
        icon.Height = size;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        return icon;
    }
}

/// <summary>XAML-friendly icon host. Set Kind and Width/Height; Foreground follows the theme.</summary>
public sealed class ToolIcon : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(ToolIcon), new PropertyMetadata("pdf", OnKindChanged));

    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public ToolIcon()
    {
        Width = 20;
        Height = 20;
        IsHitTestVisible = false;
        RenderIcon();
    }

    private static void OnKindChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((ToolIcon)sender).RenderIcon();

    private void RenderIcon()
    {
        var icon = ToolIcons.Create(Kind, 24);
        icon.SetBinding(IconElement.ForegroundProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
        Content = new Viewbox { Child = icon };
    }
}
