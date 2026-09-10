using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Shunshou.Deployment;

internal sealed class PackageContent : IDisposable
{
    internal const string CurrentExecutableName = "ShunshouToolbox.exe";
    internal const string LegacyExecutableName = "顺手工具箱.exe";
    internal const string CurrentResourceName = "ShunshouToolbox.pri";
    internal PackageManifest Manifest { get; }
    internal Dictionary<string, PackageFile> Files { get; }
    internal HashSet<string> Directories { get; }
    private readonly FileStream _stream;
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;

    private PackageContent(FileStream stream, ZipArchive archive, PackageManifest manifest,
        Dictionary<string, PackageFile> files, Dictionary<string, ZipArchiveEntry> entries, HashSet<string> directories)
        => (_stream, _archive, Manifest, Files, _entries, Directories) = (stream, archive, manifest, files, entries, directories);

    internal static async Task<PackageContent> OpenAsync(DeploymentRequest request, CancellationToken ct)
    {
        if (!PathSafety.IsHash(request.ExpectedZipSha256)) throw new InvalidDataException("安装包校验值无效。");
        PathSafety.NoLinks(request.ZipPath);
        var stream = new FileStream(request.ZipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        ZipArchive? archive = null;
        try
        {
            if (stream.Length > PathSafety.MaxPackageBytes) throw new InvalidDataException("安装包过大。");
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (!PathSafety.HashEquals(sha, request.ExpectedZipSha256)) throw new InvalidDataException("安装包已损坏，SHA-256 校验失败。原来的软件未改变。");
            stream.Position = 0;
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > 20_000) throw new InvalidDataException("安装包文件数量超出安全上限。");
            var manifestEntries = archive.Entries.Where(e => e.FullName.EndsWith("/" + PathSafety.ManifestName, StringComparison.Ordinal)
                && e.FullName.Count(c => c == '/') == 1).ToList();
            if (manifestEntries.Count != 1) throw new InvalidDataException("未找到唯一的软件安装清单。");
            var zipRoot = manifestEntries[0].FullName.Split('/')[0];
            PathSafety.Relative(zipRoot);
            if (request.ZipRoot is { } expectedRoot && !zipRoot.Equals(expectedRoot, StringComparison.Ordinal))
                throw new InvalidDataException("安装包根目录与发布信息不一致。");
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long bytes = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var path = entry.FullName;
                if (!path.StartsWith(zipRoot + "/", StringComparison.Ordinal)) throw new InvalidDataException("安装包含额外根目录。");
                var directory = path.EndsWith('/');
                var relative = path[(zipRoot.Length + 1)..].TrimEnd('/');
                if (relative.Length == 0 && directory) continue;
                PathSafety.Relative(relative);
                if (!allNames.Add(relative)) throw new InvalidDataException("安装包存在重复或大小写冲突路径：" + relative);
                var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixType is not (0 or 0x8000 or 0x4000)
                    || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("安装包包含链接或特殊文件。");
                if (directory) directories.Add(relative);
                else
                {
                    bytes = checked(bytes + entry.Length);
                    if (bytes > PathSafety.MaxPackageBytes) throw new InvalidDataException("安装包解压体积超出安全上限。");
                    entries.Add(relative, entry);
                }
            }
            await using var input = manifestEntries[0].Open();
            if (manifestEntries[0].Length > 16 * 1024 * 1024) throw new InvalidDataException("安装清单过大。");
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, ct);
            var manifestBytes = buffer.ToArray();
            var manifest = ParseManifest(manifestBytes, request.Architecture);
            if (request.ExpectedVersion is { } expectedVersion && !manifest.Version.Equals(expectedVersion, StringComparison.Ordinal))
                throw new InvalidDataException("安装包版本与发布信息不一致。");
            var files = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            files.Add(PathSafety.ManifestName, new PackageFile(PathSafety.ManifestName, manifestBytes.Length, Convert.ToHexString(SHA256.HashData(manifestBytes))));
            if (entries.Count != files.Count || entries.Keys.Any(p => !files.ContainsKey(p)))
                throw new InvalidDataException("安装包与文件清单不一致。");
            foreach (var file in files.Values)
            {
                if (!entries.TryGetValue(file.Path, out var entry) || entry.Length != file.Bytes)
                    throw new InvalidDataException("安装包缺少文件或大小不匹配：" + file.Path);
                await using var content = entry.Open();
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(content, ct));
                if (!PathSafety.HashEquals(actual, file.Sha256)) throw new InvalidDataException("安装包文件已损坏：" + file.Path);
                AddParents(directories, file.Path);
            }
            if (directories.Any(files.ContainsKey)) throw new InvalidDataException("安装包包含同名文件与文件夹。");
            if (directories.Any(IsData)) throw new InvalidDataException("安装包不能包含用户 data 目录。");
            return new PackageContent(stream, archive, manifest, files, entries, directories);
        }
        catch { archive?.Dispose(); await stream.DisposeAsync(); throw; }
    }

    internal static PackageManifest ParseManifest(byte[] bytes, string architecture)
    {
        PackageManifest manifest;
        try { manifest = JsonSerializer.Deserialize<PackageManifest>(bytes) ?? throw new InvalidDataException("软件清单为空。"); }
        catch (JsonException ex) { throw new InvalidDataException("软件清单无法读取。", ex); }
        if (manifest.Product != "顺手工具箱" || manifest.Architecture != architecture || manifest.Architecture != "win-x64"
            || manifest.Files is null || manifest.Files.Count is < 3 or > 20_000)
            throw new InvalidDataException("此文件夹或安装包不是受支持的顺手工具箱版本。");
        _ = VersionNumber(manifest.Version);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null) throw new InvalidDataException("软件清单包含空文件记录。");
            PathSafety.Relative(file.Path);
            if (!paths.Add(file.Path) || file.Path.Equals(PathSafety.ManifestName, StringComparison.OrdinalIgnoreCase)
                || IsData(file.Path) || !PathSafety.IsHash(file.Sha256) || file.Bytes < 0)
                throw new InvalidDataException("软件清单包含重复、保留或无效文件记录。");
            total = checked(total + file.Bytes);
            if (total > PathSafety.MaxPackageBytes) throw new InvalidDataException("软件清单体积超出安全上限。");
            AddParents(parents, file.Path);
        }
        if (parents.Any(paths.Contains)) throw new InvalidDataException("软件清单文件路径相互冲突。");
        if ((!paths.Contains(CurrentExecutableName) && !paths.Contains(LegacyExecutableName))
            || !paths.Contains("Shunshou.App.dll") || !paths.Contains("Shunshou.Core.dll"))
            throw new InvalidDataException("此文件夹缺少顺手工具箱产品标识文件。");
        if (paths.Contains(CurrentExecutableName) && !paths.Contains(CurrentResourceName))
            throw new InvalidDataException("此安装包缺少与主程序同名的界面资源文件。");
        return manifest;
    }

    internal static Version VersionNumber(string? version)
    {
        if (version is null || version.Split('.').Length is < 3 or > 4
            || version.Any(c => !char.IsAsciiDigit(c) && c != '.') || !Version.TryParse(version, out var parsed))
            throw new InvalidDataException("软件版本格式无效。");
        return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
    }

    internal async Task ExtractAsync(string stage, IProgress<DeploymentProgress>? progress, CancellationToken ct)
    {
        foreach (var directory in Directories.OrderBy(x => x.Count(c => c == '/')))
        {
            ct.ThrowIfCancellationRequested();
            var full = PathSafety.Under(stage, directory);
            PathSafety.NoLinks(full);
            Directory.CreateDirectory(full);
        }
        var index = 0;
        foreach (var file in Files.Values)
        {
            ct.ThrowIfCancellationRequested();
            var full = PathSafety.Under(stage, file.Path);
            PathSafety.NoLinks(full);
            await using (var source = _entries[file.Path].Open())
            await using (var target = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(target, ct);
                target.Flush(flushToDisk: true);
            }
            progress?.Report(new("正在解压并校验软件文件…", 10 + ++index * 35 / Files.Count));
        }
    }

    internal static bool IsData(string path) => path.Equals("data", StringComparison.OrdinalIgnoreCase) || path.StartsWith("data/", StringComparison.OrdinalIgnoreCase);
    internal static void AddParents(HashSet<string> directories, string path)
    {
        for (var index = path.LastIndexOf('/'); index >= 0; index = path.LastIndexOf('/', index - 1))
        {
            directories.Add(path[..index]);
            if (index == 0) break;
        }
    }

    public void Dispose() { _archive.Dispose(); _stream.Dispose(); }
}
