using Microsoft.Win32;

namespace Shunshou.DesktopIntegration;

public enum StartupRegistrationState { Disabled, Enabled, PointsElsewhere, Unavailable }

/// <summary>Where the current-user start-up entry is stored. Injected so tests never touch the real registry.</summary>
public interface IStartupValueStore
{
    string? Read(string name);
    bool Write(string name, string value);
    bool Delete(string name);
}

/// <summary>Stores the start-up entry under HKCU, so no administrator rights and no Windows service are involved.</summary>
public sealed class UserRunStartupStore : IStartupValueStore
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _keyPath;

    public UserRunStartupStore(string? keyPath = null) => _keyPath = keyPath ?? RunKeyPath;

    public string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
        return key?.GetValue(name) as string;
    }

    public bool Write(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
        key.SetValue(name, value, RegistryValueKind.String);
        return true;
    }

    public bool Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        if (key is null) return true;
        if (key.GetValue(name) is not null) key.DeleteValue(name, throwOnMissingValue: false);
        return true;
    }
}

/// <summary>
/// Optional "start with Windows" entry for the current user. The value starts the normal package
/// entry point with <see cref="StartupArgument"/>, which keeps the window minimised until wanted.
/// </summary>
public sealed class StartupRegistration
{
    public const string ValueName = PackageIdentity.ProductName;
    public const string StartupArgument = "--startup";

    private readonly IStartupValueStore _store;

    public StartupRegistration(IStartupValueStore? store = null) => _store = store ?? new UserRunStartupStore();

    public static string ExpectedValue(string executablePath) =>
        "\"" + Path.GetFullPath(executablePath) + "\" " + StartupArgument;

    public StartupRegistrationState Read(string executablePath)
    {
        try
        {
            string? current = _store.Read(ValueName);
            if (string.IsNullOrWhiteSpace(current)) return StartupRegistrationState.Disabled;
            return string.Equals(current.Trim(), ExpectedValue(executablePath), StringComparison.OrdinalIgnoreCase)
                ? StartupRegistrationState.Enabled
                : StartupRegistrationState.PointsElsewhere;
        }
        catch (Exception ex) when (IsStoreFailure(ex)) { return StartupRegistrationState.Unavailable; }
    }

    public bool Enable(string executablePath)
    {
        try
        {
            string expected = ExpectedValue(executablePath);
            if (!_store.Write(ValueName, expected)) return false;
            return string.Equals(_store.Read(ValueName)?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsStoreFailure(ex)) { return false; }
    }

    /// <summary>Removes only an entry that belongs to this product; a same-named entry from elsewhere is kept.</summary>
    public bool Disable()
    {
        try
        {
            string? current = _store.Read(ValueName);
            if (string.IsNullOrWhiteSpace(current)) return true;
            if (!IsOwnedEntry(current)) return false;
            if (!_store.Delete(ValueName)) return false;
            return string.IsNullOrWhiteSpace(_store.Read(ValueName));
        }
        catch (Exception ex) when (IsStoreFailure(ex)) { return false; }
    }

    /// <summary>
    /// Keeps an enabled entry pointing at the running copy, so moving the folder never leaves a broken
    /// start-up item behind. A disabled entry is never created here.
    /// </summary>
    public StartupRegistrationState SyncOnLaunch(string executablePath, out bool repaired)
    {
        repaired = false;
        var state = Read(executablePath);
        if (state != StartupRegistrationState.PointsElsewhere) return state;
        if (!IsOwnedEntry(_store.Read(ValueName))) return state;
        repaired = Enable(executablePath);
        return repaired ? StartupRegistrationState.Enabled : state;
    }

    /// <summary>A value written by this product always names one of our executables.</summary>
    private static bool IsOwnedEntry(string? value) => TryGetOwnedExecutablePath(value, out _);

    /// <summary>
    /// Reads the executable out of a start-up value, but only when it belongs to this product.
    /// Quoted command lines keep paths with spaces intact; an unquoted value ends before its switch.
    /// </summary>
    public static bool TryGetOwnedExecutablePath(string? value, out string executablePath)
    {
        executablePath = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        string text = value.Trim();
        string candidate;
        if (text.StartsWith('"'))
        {
            int closing = text.IndexOf('"', 1);
            if (closing <= 1) return false;
            candidate = text[1..closing];
        }
        else
        {
            int argument = text.IndexOf(" --", StringComparison.Ordinal);
            candidate = argument > 0 ? text[..argument] : text;
        }
        try
        {
            if (!IsOwnedExecutableName(Path.GetFileName(candidate))) return false;
            executablePath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>
    /// The package entry point, the legacy Chinese entry point, and the private runtime host used by
    /// unpackaged development builds all belong to this product.
    /// </summary>
    private static bool IsOwnedExecutableName(string? name) =>
        PackageIdentity.IsSupportedExecutableName(name) ||
        string.Equals(name, "Shunshou.App.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsStoreFailure(Exception exception) => exception is
        UnauthorizedAccessException or IOException or System.Security.SecurityException or NotSupportedException or InvalidOperationException;
}
