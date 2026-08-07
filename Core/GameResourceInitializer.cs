using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DWOriginsAssetTool.Core;

public sealed record GameResourceInitializationProgress(int PackageDone, int PackageTotal,
    int RecordDone, int RecordTotal, string CurrentFile, long OutputBytes,
    double OverallPercent = 0, string Stage = "解包游戏资源");

public sealed record GameResourceInitializationResult(int PackageCount, int ResourceCount,
    long OutputBytes, int ErrorCount, int OfficialNameCount, int GameAnimationCount,
    int CameraCount, int DecodedAnimationCount, int ResumedPackageCount, string Destination);

public sealed record EmbeddedResourceIndexInfo(int ResourcePathCount, int CriticalNameCount);

/// <summary>
/// Rebuilds the shipped expanded resource tree from the retail FData packages.
/// The embedded index contains paths/identifiers only; no copyrighted game data.
/// </summary>
public static class GameResourceInitializer
{
    private const int InitializationVersion = 2;
    private const string PathIndexResource = "DWOriginsAssetTool.Assets.dworigins_resource_paths.tsv.gz";
    private const string CriticalNamesResource = "DWOriginsAssetTool.Assets.dworigins_critical_names.tsv";

    public static string ExpandedRoot(string gameRoot) => Path.Combine(gameRoot, "File", "ASSET",
        "FDataPackage", "Dev", "fdata_out");

    public static bool IsComplete(string gameRoot)
    {
        var root = ExpandedRoot(gameRoot);
        var hasCriticalFiles = File.Exists(Path.Combine(root, "KIDSSystemResource", "kidsobjdb",
                   "CE1CommonResource.motor.kidsobjdb")) &&
               File.Exists(Path.Combine(root, "KIDSSystemResource", "kidsobjdb",
                   "CE1CommonResource.motor.kidssingletondb")) &&
               File.Exists(Path.Combine(root, "KIDSSystemResource", "kidsobjdb",
                   "ProjectCommonResource.motor.kidsscndb"));
        if (!hasCriticalFiles) return false;

        // New initializations create manifests before extracting the first package and
        // write the completion marker only after all catalogs pass validation.  This
        // keeps an interrupted run resumable even if its KIDS databases already exist.
        if (Directory.Exists(Path.Combine(root, "_manifests")))
        {
            try
            {
                using var marker = JsonDocument.Parse(File.ReadAllText(
                    Path.Combine(root, ".dworigins-initialized.json")));
                return marker.RootElement.GetProperty("Version").GetInt32() >= InitializationVersion &&
                       marker.RootElement.GetProperty("Errors").GetInt32() == 0;
            }
            catch { return false; }
        }

        // Expanded trees produced by older versions did not have a marker.
        return true;
    }

    public static IReadOnlyList<string> FindPackages(string gameRoot)
    {
        var root = Path.Combine(gameRoot, "File", "ASSET", "FDataPackage");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*.fdata", new EnumerationOptions
            { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static EmbeddedResourceIndexInfo GetEmbeddedIndexInfo()
    {
        var paths = LoadPathIndex();
        var criticalNames = LoadCriticalNames();
        return new(paths.Count, criticalNames.Count);
    }

    public static async Task<GameResourceInitializationResult> InitializeAsync(string gameRoot,
        IProgress<GameResourceInitializationProgress>? progress = null,
        CancellationToken token = default)
    {
        var packages = FindPackages(gameRoot);
        if (packages.Count == 0) throw new InvalidOperationException("游戏目录中没有找到 FData 资源包。");
        var destination = ExpandedRoot(gameRoot);
        Directory.CreateDirectory(destination);
        var completionMarker = Path.Combine(destination, ".dworigins-initialized.json");
        if (File.Exists(completionMarker)) File.Delete(completionMarker);
        var paths = LoadPathIndex();
        var criticalNames = LoadCriticalNames();
        var criticalSizes = LoadCriticalSizes();
        var extensions = paths.ToDictionary(x => x.Key, x => Path.GetExtension(x.Value),
            EqualityComparer<uint>.Default);
        var manifests = Path.Combine(destination, "_manifests");
        Directory.CreateDirectory(manifests);
        progress?.Report(new(0, packages.Count, 0, 0, "正在分析资源包索引", 0, 0, "准备初始化"));
        var preferredRecords = BuildPreferredRecordSets(packages);
        long outputBytes = 0; var files = 0; var errors = 0; var resumedPackages = 0;
        for (var packageIndex = 0; packageIndex < packages.Count; packageIndex++)
        {
            token.ThrowIfCancellationRequested();
            var package = packages[packageIndex];
            var localPackageIndex = packageIndex;
            var selectedIds = preferredRecords.GetValueOrDefault(package) ?? [];
            var manifestName = ManifestFileName(gameRoot, package);
            var manifestPath = Path.Combine(manifests, manifestName);
            if (TryReadCompletedManifest(manifestPath, package, destination,
                    out var completedFiles, out var completedBytes))
            {
                files += completedFiles;
                outputBytes += completedBytes;
                resumedPackages++;
                progress?.Report(new(packageIndex + 1, packages.Count, 1, 1,
                    $"已完成，无需重新解包：{Path.GetFileName(package)}", outputBytes,
                    (packageIndex + 1d) / packages.Count * 94d, "校验已完成资源包"));
                continue;
            }
            var packageProgress = new Progress<(int Done, int Total, string Name)>(value =>
                progress?.Report(new(localPackageIndex + 1, packages.Count, value.Done, value.Total,
                    value.Name, outputBytes,
                    (localPackageIndex + (value.Total == 0 ? 0d : value.Done / (double)value.Total)) /
                    packages.Count * 94d, "解包游戏资源")));
            var result = await FDataUnpacker.ExtractAsync(package, destination, extensions,
                packageProgress, token, knownRelativePaths: paths, manifestDirectory: manifests,
                contentNames: criticalNames,
                shouldReadRecord: record => selectedIds.Contains(record.ResourceId) ||
                    criticalSizes.Contains(record.UncompressedSize),
                includeRecord: (record, contentHash) => selectedIds.Contains(record.ResourceId) ||
                    contentHash is not null && criticalNames.ContainsKey(contentHash),
                manifestFileName: manifestName, manifestPolicyVersion: InitializationVersion);
            outputBytes += result.OutputBytes;
            files += result.ExtractedCount;
            errors += result.Errors.Count;
        }

        if (errors > 0)
            throw new InvalidDataException($"资源解包出现 {errors:N0} 个错误，未写入完成标记。再次初始化会从未完成的资源包继续。");

        progress?.Report(new(packages.Count, packages.Count, 0, 1,
            "正在恢复正式名称", outputBytes, 94d, "构建资源目录"));
        var catalog = await KidsNameCatalogReader.BuildAsync(gameRoot, token: token);
        progress?.Report(new(packages.Count, packages.Count, 0, 1,
            "正在恢复角色、武器与动作归属", outputBytes, 96d, "构建资源分类"));
        var animations = KidsAnimationCatalog.Build(gameRoot);
        progress?.Report(new(packages.Count, packages.Count, 0, 1,
            "正在建立动画与镜头关联", outputBytes, 97d, "构建镜头关联"));
        var cameras = ActionCameraCatalog.Build(gameRoot);
        if (catalog.Entries.Count == 0 || catalog.Bindings is not { Count: > 0 } ||
            animations.Count == 0 || cameras.Count == 0)
            throw new InvalidDataException("资源已解出，但正式名称、动作表或镜头关联校验未通过。请确认游戏文件版本完整。 ");
        var decodedAnimations = ValidateAnimations(destination, token, (done, total, file) =>
            progress?.Report(new(packages.Count, packages.Count, done, total, file, outputBytes,
                98d + (total == 0 ? 0d : done / (double)total * 2d), "校验动画完整性")));
        if (decodedAnimations < 6_856)
            throw new InvalidDataException($"动画完整性校验仅找到 {decodedAnimations:N0} 个动画，低于基础游戏应有的 6,856 个，未写入完成标记。");
        var marker = new
        {
            Version = InitializationVersion, CreatedAt = DateTimeOffset.Now, Packages = packages.Count,
            Resources = files, OutputBytes = outputBytes, Errors = errors,
            OfficialNames = catalog.Bindings.Count, GameAnimations = animations.Count, Cameras = cameras.Count,
            DecodedAnimations = decodedAnimations, ResumedPackages = resumedPackages,
            LanguagePolicy = "zhCN > base > enUS > jaJP"
        };
        await File.WriteAllTextAsync(completionMarker,
            JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }), token);
        return new(packages.Count, files, outputBytes, errors, catalog.Bindings.Count,
            animations.Count, cameras.Count, decodedAnimations, resumedPackages, destination);
    }

    private static Dictionary<string, HashSet<uint>> BuildPreferredRecordSets(IReadOnlyList<string> packages)
    {
        var candidates = new Dictionary<uint, List<string>>();
        foreach (var package in packages)
            foreach (var record in FDataUnpacker.ReadIndex(package))
            {
                if (!candidates.TryGetValue(record.ResourceId, out var list))
                    candidates[record.ResourceId] = list = [];
                list.Add(package);
            }
        var result = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in candidates)
        {
            var package = pair.Value.OrderBy(PackageLanguagePriority)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).First();
            if (!result.TryGetValue(package, out var ids)) result[package] = ids = [];
            ids.Add(pair.Key);
        }
        return result;
    }

    private static int PackageLanguagePriority(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(x => x.Equals("zhCN", StringComparison.OrdinalIgnoreCase))) return 0;
        if (parts.Any(x => x.Equals("enUS", StringComparison.OrdinalIgnoreCase))) return 2;
        if (parts.Any(x => x.Equals("jaJP", StringComparison.OrdinalIgnoreCase))) return 3;
        return 1;
    }

    private static string ManifestFileName(string gameRoot, string package)
    {
        var packageRoot = Path.Combine(gameRoot, "File", "ASSET", "FDataPackage");
        var relative = Path.GetRelativePath(packageRoot, package);
        var safe = string.Concat(relative.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative)))[..12].ToLowerInvariant();
        return $"{safe}_{fingerprint}.manifest.json";
    }

    private static bool TryReadCompletedManifest(string manifestPath, string package, string destination,
        out int files, out long bytes)
    {
        files = 0; bytes = 0;
        try
        {
            if (!File.Exists(manifestPath)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            var source = new FileInfo(package);
            if (!root.TryGetProperty("Complete", out var complete) || !complete.GetBoolean() ||
                !root.TryGetProperty("SourceLength", out var length) || length.GetInt64() != source.Length ||
                !root.TryGetProperty("SourceLastWriteTimeUtc", out var modified) ||
                modified.GetDateTime().ToUniversalTime() != source.LastWriteTimeUtc ||
                !root.TryGetProperty("PolicyVersion", out var policy) || policy.GetInt32() != InitializationVersion ||
                !root.TryGetProperty("Errors", out var manifestErrors) || manifestErrors.GetArrayLength() != 0)
                return false;
            files = root.GetProperty("Extracted").GetInt32();
            bytes = root.GetProperty("OutputBytes").GetInt64();
            if (!root.TryGetProperty("SelectedRecords", out var selected) || files != selected.GetInt32() ||
                !root.TryGetProperty("Files", out var manifestFiles) || manifestFiles.GetArrayLength() != files)
                return false;
            foreach (var file in manifestFiles.EnumerateArray())
            {
                var relative = file.GetProperty("RelativePath").GetString();
                if (string.IsNullOrWhiteSpace(relative)) return false;
                var fullPath = Path.GetFullPath(Path.Combine(destination, relative));
                if (!fullPath.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath) ||
                    new FileInfo(fullPath).Length != file.GetProperty("Size").GetInt64()) return false;
                using var existing = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.SequentialScan);
                var hash = Convert.ToHexString(SHA256.HashData(existing));
                if (!hash.Equals(file.GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static int ValidateAnimations(string destination, CancellationToken token,
        Action<int, int, string>? report = null)
    {
        var animationPaths = Directory.EnumerateFiles(destination, "*", new EnumerationOptions
            { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(path => Path.GetExtension(path).Equals(".g1a", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".g2a", StringComparison.OrdinalIgnoreCase)).ToArray();
        var errors = new List<string>();
        for (var index = 0; index < animationPaths.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var path = animationPaths[index];
            try { _ = G1Parsers.ReadAnimationClip(path); }
            catch (Exception ex) { if (errors.Count < 20) errors.Add($"{Path.GetRelativePath(destination, path)}: {ex.Message}"); }
            if (index == 0 || (index + 1) % 32 == 0 || index + 1 == animationPaths.Length)
                report?.Invoke(index + 1, animationPaths.Length, Path.GetFileName(path));
        }
        if (errors.Count > 0)
            throw new InvalidDataException("动画完整性校验失败：\n" + string.Join("\n", errors));
        return animationPaths.Length;
    }

    private static Dictionary<uint, string> LoadPathIndex()
    {
        using var resource = OpenResource(PathIndexResource);
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var result = new Dictionary<uint, string>();
        while (reader.ReadLine() is { } line)
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0 || !uint.TryParse(line.AsSpan(0, tab),
                    System.Globalization.NumberStyles.HexNumber, null, out var id)) continue;
            result.TryAdd(id, line[(tab + 1)..]);
        }
        return result;
    }

    private static Dictionary<string, string> LoadCriticalNames()
    {
        using var resource = OpenResource(CriticalNamesResource);
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split('\t', 3);
            if (parts.Length == 3) result.TryAdd(parts[0], parts[2]);
        }
        return result;
    }

    private static HashSet<long> LoadCriticalSizes()
    {
        using var resource = OpenResource(CriticalNamesResource);
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var result = new HashSet<long>();
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split('\t', 3);
            if (parts.Length == 3 && long.TryParse(parts[1], out var size)) result.Add(size);
        }
        return result;
    }

    private static Stream OpenResource(string name) => typeof(GameResourceInitializer).Assembly
        .GetManifestResourceStream(name) ?? throw new FileNotFoundException("内置游戏目录索引缺失", name);

    public static string ContentFingerprint(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
