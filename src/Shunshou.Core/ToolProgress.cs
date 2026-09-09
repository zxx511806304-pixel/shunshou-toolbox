namespace Shunshou.Core;

public sealed record ToolProgress(double Percent, string Message);

public static class OutputPaths
{
    public static string Unique(string directory, string filename)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, filename);
        var stem = Path.GetFileNameWithoutExtension(filename);
        var extension = Path.GetExtension(filename);
        for (var i = 2; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(directory, $"{stem} ({i}){extension}");
        return path;
    }
}
