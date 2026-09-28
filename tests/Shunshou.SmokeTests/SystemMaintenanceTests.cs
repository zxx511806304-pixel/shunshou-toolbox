using System.Text.RegularExpressions;
using Shunshou.Core;

namespace Shunshou.SmokeTests;

/// <summary>Covers the 1.4.0 hijack scan on the real machine: junction-safe traversal and
/// home-page false-positive fixes, plus synthetic Chrome/Edge preference parsing cases.</summary>
public static class SystemMaintenanceTests
{
    // The old string-search path reported 24+ char hex identifiers (new-tab-page tokens) as URLs.
    private static readonly Regex LongHex = new("[0-9A-Fa-f]{24,}", RegexOptions.Compiled);

    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await ScanOnThisMachineAsync(root);
        PreferenceParsing();
    }

    private static async Task ScanOnThisMachineAsync(string root)
    {
        var service = new HijackScanService();
        var progress = new Progress<string>(message => Console.WriteLine("  scan: " + message));
        HijackScanResult result = await service.ScanAsync(progress, CancellationToken.None);

        foreach (HijackFinding finding in result.Findings)
            Console.WriteLine($"  finding [{finding.Kind}] {finding.Title} :: {finding.Detail}");
        Console.WriteLine($"  skipped directories: {result.SkippedDirectories}");

        File.WriteAllText(Path.Combine(root, "hijack-scan.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                result.SkippedDirectories,
                Findings = result.Findings.Select(f => new { f.Kind, f.Title, f.Detail, f.CanAutoFix })
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        CompressionTests.Check(!result.Findings.Any(f =>
            f.Detail.Contains("LinkId=255141", StringComparison.OrdinalIgnoreCase)),
            "the go.microsoft.com default first-run page is not flagged");
        CompressionTests.Check(!result.Findings.Any(f => LongHex.IsMatch(f.Detail)),
            "hex-looking new-tab identifiers are not flagged as home pages");
        Console.WriteLine("PASS hijack scan: completes on this machine without known false positives");
    }

    private static void PreferenceParsing()
    {
        // A "homepage" key nested under extension settings must not be treated as the home page.
        string nested = """
            {"extensions":{"settings":{"abc":{"homepage":"http://evil.example/x"}}}}
            """;
        CompressionTests.Check(!HijackScanService.TryReadConfiguredHomepage(nested, out _),
            "nested homepage keys are ignored");

        // A real top-level home page is read regardless of whether it is allowed.
        string top = """{"homepage":"https://www.bing.com/"}""";
        CompressionTests.Check(HijackScanService.TryReadConfiguredHomepage(top, out string value)
            && value == "https://www.bing.com/", "top-level homepage string is read");

        // A non-string homepage (number/object) must not be reported.
        string typed = """{"homepage":1234}""";
        CompressionTests.Check(!HijackScanService.TryReadConfiguredHomepage(typed, out _),
            "non-string homepage is ignored");

        // Corrupt JSON yields false instead of crashing the scan.
        CompressionTests.Check(!HijackScanService.TryReadConfiguredHomepage("{ broken", out _),
            "malformed JSON is ignored");

        Console.WriteLine("PASS preference parsing: top-level only, typed and malformed input");
    }
}
