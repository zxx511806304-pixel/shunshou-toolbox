using Shunshou.SmokeTests;

var root = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? Path.Combine("artifacts", "smoke", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
Directory.CreateDirectory(root);
Console.WriteLine($"Test artifacts: {root}");
try
{
    if (args.Contains("--inputs")) await InputTests.RunAsync(root);
    else if (args.Contains("--compression-boundary")) await CompressionTests.RunBoundaryAsync(root);
    else if (args.Contains("--media-precision")) await MediaTests.RunPrecisionAsync(root);
    else if (args.Contains("--media")) await MediaTests.RunAsync(root);
    else if (args.Contains("--files")) await FileTests.RunAsync(root);
    else if (args.Contains("--pdf")) await PdfTests.RunAsync(root);
    else if (args.Contains("--ocr")) await OcrTests.RunAsync(root);
    else if (args.Contains("--compression")) await CompressionTests.RunAsync(root);
    else if (args.Contains("--images")) await ImageTests.RunAsync(root);
    else await TestSuite.RunAsync(root);
    Console.WriteLine("ALL SELECTED TESTS PASSED");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
