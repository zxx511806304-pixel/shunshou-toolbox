using Shunshou.Core;
using System.Text.Json;

var workspace = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("artifacts", "appearance-verification"));
var fixture = Path.Combine(workspace, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var settings = Path.Combine(fixture, "data", "appearance.json");
var checks = new List<string>();

void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks.Add(description);
}

Check(AppearancePreferences.Load(settings) is null && !Directory.Exists(Path.GetDirectoryName(settings)),
    "Missing preference falls back without creating a data folder");
Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
foreach (var invalid in new[] { "{broken", "null", "[]", "{\"version\":1,\"theme\":\"other\"}",
    "{\"version\":2,\"theme\":\"dark\"}", "{\"version\":\"one\",\"theme\":\"dark\"}", new string(' ', 4097) })
{
    File.WriteAllText(settings, invalid);
    if (AppearancePreferences.Load(settings) is not null) throw new InvalidOperationException("Damaged preference was accepted");
}
checks.Add("Malformed, unknown, wrong-version and oversized preferences safely fall back");
Check(AppearancePreferences.TrySave(AppearanceTheme.Dark, settings) && AppearancePreferences.Load(settings) == AppearanceTheme.Dark,
    "Dark preference survives a new read and replaces a damaged preference");
var unrelated = Path.Combine(Path.GetDirectoryName(settings)!, "keep.json");
File.WriteAllText(unrelated, "fixture-user-setting");
Check(AppearancePreferences.TrySave(AppearanceTheme.Light, settings) && AppearancePreferences.Load(settings) == AppearanceTheme.Light &&
      File.ReadAllText(unrelated) == "fixture-user-setting", "Light replaces dark without changing other user data");
Check(Directory.GetFiles(Path.GetDirectoryName(settings)!, ".appearance-*.tmp").Length == 0,
    "Successful replacement leaves no temporary files");
var directoryCollision = Path.Combine(fixture, "occupied");
Directory.CreateDirectory(directoryCollision);
Check(!AppearancePreferences.TrySave(AppearanceTheme.Dark, directoryCollision) &&
      Directory.Exists(directoryCollision) && Directory.GetFiles(fixture, ".appearance-*.tmp").Length == 0,
    "Unwritable destination fails safely and cleans up its temporary file");
var before = File.ReadAllBytes(settings);
Check(!AppearancePreferences.TrySave((AppearanceTheme)42, settings) && File.ReadAllBytes(settings).SequenceEqual(before),
    "Invalid theme cannot overwrite a saved preference");
AppPaths.DisableLogging();
Check(!AppearancePreferences.TrySave(AppearanceTheme.Dark, settings) && File.ReadAllBytes(settings).SequenceEqual(before),
    "Recovery mode never writes a preference");
var summary = new { Passed = true, Checks = checks, Fixture = fixture };
File.WriteAllText(Path.Combine(workspace, "appearance-verification.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Appearance preferences: {checks.Count} checks passed. Evidence: {Path.Combine(workspace, "appearance-verification.json")}");
