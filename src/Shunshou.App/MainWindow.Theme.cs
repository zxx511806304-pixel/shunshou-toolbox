using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Shunshou.Core;
using Windows.UI.ViewManagement;
using System.Runtime.InteropServices;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    private AccessibilitySettings? _appearanceAccessibility;
    private bool _appearanceDisposed;
    private bool _appearanceVerificationLaunch;
    private bool _appearanceContrastSubscribed;

    private void InitializeAppearance()
    {
        _appearanceVerificationLaunch = Environment.GetCommandLineArgs().Skip(1).Any(argument =>
            argument.StartsWith("--verify", StringComparison.Ordinal) || argument.StartsWith("--screenshot", StringComparison.Ordinal));
        var saved = _appearanceVerificationLaunch ? null : AppearancePreferences.Load();
        RootLayout.RequestedTheme = saved switch
        {
            AppearanceTheme.Light => ElementTheme.Light,
            AppearanceTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        RootLayout.ActualThemeChanged += Appearance_ActualThemeChanged;
        try
        {
            _appearanceAccessibility = new AccessibilitySettings();
            _appearanceAccessibility.HighContrastChanged += Appearance_HighContrastChanged;
            _appearanceContrastSubscribed = true;
        }
        catch (COMException)
        {
            // Some unpackaged Windows 10 hosts cannot subscribe to this notification.
            // XAML still applies system contrast resources; refresh caption colors on activation.
        }
        Activated += Appearance_Activated;
        RefreshAppearanceControls();
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs args)
    {
        AppearanceTheme next;
        if (RootLayout.RequestedTheme == ElementTheme.Default)
        {
            // System → Light
            RootLayout.RequestedTheme = ElementTheme.Light;
            next = AppearanceTheme.Light;
        }
        else if (RootLayout.RequestedTheme == ElementTheme.Dark)
        {
            // Dark → System
            RootLayout.RequestedTheme = ElementTheme.Default;
            next = AppearanceTheme.System;
        }
        else
        {
            // Light → Dark
            RootLayout.RequestedTheme = ElementTheme.Dark;
            next = AppearanceTheme.Dark;
        }
        RefreshAppearanceControls();
        if (!_appearanceVerificationLaunch && !AppPaths.LoggingDisabled)
            AppearancePreferences.TrySave(next);
    }

    private void ThemeFollowSystem_Click(object sender, RoutedEventArgs e)
    {
        RootLayout.RequestedTheme = ElementTheme.Default;
        RefreshAppearanceControls();
        if (!_appearanceVerificationLaunch && !AppPaths.LoggingDisabled)
            AppearancePreferences.TrySave(AppearanceTheme.System);
    }

    private void Appearance_ActualThemeChanged(FrameworkElement sender, object args) => RefreshAppearanceControls();

    private void Appearance_Activated(object sender, WindowActivatedEventArgs args) => RefreshAppearanceControls();

    private void Appearance_HighContrastChanged(AccessibilitySettings sender, object args)
        => DispatcherQueue.TryEnqueue(() => { if (!_appearanceDisposed) RefreshAppearanceControls(); });

    private void RefreshAppearanceControls()
    {
        if (_appearanceDisposed) return;
        bool dark = RootLayout.ActualTheme == ElementTheme.Dark;
        bool isSystem = RootLayout.RequestedTheme == ElementTheme.Default;
        var label = isSystem ? "跟随系统" : dark ? "切换为浅色主题" : "切换为深色主题";
        if (isSystem)
        {
            ThemeToggleIcon.Kind = dark ? "moon" : "sun";
        }
        else
        {
            ThemeToggleIcon.Kind = dark ? "sun" : "moon";
        }
        AutomationProperties.SetName(ThemeToggleButton, label);
        ToolTipService.SetToolTip(ThemeToggleButton, label);
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var titleBar = AppWindow.TitleBar;
        // Let Windows supply all caption colors while a contrast theme is enabled.
        bool contrast;
        try { contrast = _appearanceAccessibility?.HighContrast == true; }
        catch (COMException) { contrast = true; }
        titleBar.ButtonBackgroundColor = contrast ? null : Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = contrast ? null : Colors.Transparent;
        titleBar.BackgroundColor = contrast ? null : dark
            ? ColorHelper.FromArgb(255, 20, 20, 20)
            : ColorHelper.FromArgb(255, 255, 255, 255);
        titleBar.ForegroundColor = contrast ? null : dark ? Colors.White : Colors.Black;
        titleBar.ButtonForegroundColor = contrast ? null : dark ? Colors.White : Colors.Black;
        titleBar.ButtonInactiveForegroundColor = contrast ? null : Colors.Gray;
        titleBar.ButtonHoverForegroundColor = contrast ? null : dark ? Colors.White : Colors.Black;
        titleBar.ButtonPressedForegroundColor = contrast ? null : dark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverBackgroundColor = contrast ? null : dark
            ? ColorHelper.FromArgb(255, 55, 55, 55) : ColorHelper.FromArgb(255, 232, 232, 232);
        titleBar.ButtonPressedBackgroundColor = contrast ? null : dark
            ? ColorHelper.FromArgb(255, 72, 72, 72) : ColorHelper.FromArgb(255, 218, 218, 218);
    }

    private void DisposeAppearance()
    {
        _appearanceDisposed = true;
        RootLayout.ActualThemeChanged -= Appearance_ActualThemeChanged;
        Activated -= Appearance_Activated;
        if (_appearanceContrastSubscribed && _appearanceAccessibility is not null)
            _appearanceAccessibility.HighContrastChanged -= Appearance_HighContrastChanged;
        _appearanceAccessibility = null;
    }
}
