using Microsoft.Win32;

namespace Shunshou.DesktopIntegration;

public interface IInstallationRegistration
{
    string? ReadLastDirectory();
    void RememberDirectory(string directory);
}

/// <summary>A current-user location hint, not an installed-program or activation record.</summary>
public sealed class InstallationRegistry : IInstallationRegistration
{
    private const string KeyPath = @"Software\ShunshouToolbox";

    public string? ReadLastDirectory()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            var value = key?.GetValue("LastDirectory") as string;
            return PackageIdentity.IsValidDirectory(value) ? Path.GetFullPath(value!) : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { return null; }
    }

    public void RememberDirectory(string directory)
    {
        if (!PackageIdentity.IsValidDirectory(directory)) return;
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("LastDirectory", Path.GetFullPath(directory), RegistryValueKind.String);
    }
}
