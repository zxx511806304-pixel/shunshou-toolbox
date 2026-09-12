namespace Shunshou.SmokeTests;

/// <summary>Integration tests use only deterministic generated files beneath the supplied run directory.</summary>
public static class TestSuite
{
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await AppPathTests.RunAsync(root);
        await InputTests.RunAsync(root);
        await UninstallTests.RunAsync(root);
        await CompressionTests.RunAsync(root);
        await CompressionTests.RunBoundaryAsync(root);
        await ImageTests.RunAsync(root);
        await MediaTests.RunAsync(root);
        await MediaTests.RunPrecisionAsync(root);
        await PdfTests.RunAsync(root);
        await OcrTests.RunAsync(root);
        await FileTests.RunAsync(root);
        await RecoveryTests.RunAsync(root);
        Console.WriteLine("PASS: all smoke-test groups completed.");
    }
}
