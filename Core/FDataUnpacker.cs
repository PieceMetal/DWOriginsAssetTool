using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DWOriginsAssetTool.Core;

public sealed record FDataRecord(
    long Offset, long RecordSize, long CompressedSize, long UncompressedSize,
    uint TypeCode, uint ResourceId, uint SecondaryId, uint Flags);

public sealed record FDataExtracted(
    uint ResourceId, string Extension, long Size, string Path, string Sha256, long SourceOffset);

public sealed record FDataExtractResult(int RecordCount, int ExtractedCount, long OutputBytes,
    IReadOnlyList<FDataExtracted> Files, IReadOnlyList<string> Errors);

public static class FDataUnpacker
{
    private const int Alignment = 16;
    private static readonly byte[] Idrk = "IDRK0000"u8.ToArray();
    private static readonly byte[] Pdrk = "PDRK0000"u8.ToArray();

    public static IReadOnlyList<FDataRecord> ReadIndex(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        var records = new List<FDataRecord>();
        Span<byte> head = stackalloc byte[64];
        long offset = 0;
        while (offset + 16 <= stream.Length)
        {
            stream.Position = offset;
            head.Clear();
            var read = stream.Read(head);
            if (read < 16) break;
            if (head[..8].SequenceEqual(Pdrk)) { offset += 16; continue; }
            if (!head[..8].SequenceEqual(Idrk))
            {
                var found = FindNextMagic(stream, offset + Alignment, Idrk, Pdrk);
                if (found < 0) break;
                offset = found; continue;
            }

            if (read < 48) throw new InvalidDataException($"IDRK 记录头不完整：0x{offset:X}");
            var size = checked((long)U64(head, 8));
            var compressed = checked((long)U64(head, 16));
            var output = checked((long)U64(head, 24));
            if (size < 48 || offset + size > stream.Length)
                throw new InvalidDataException($"IDRK 记录大小无效：0x{offset:X}, {size:N0}");
            records.Add(new FDataRecord(offset, size, compressed, output,
                U32(head, 32), U32(head, 36), U32(head, 40), U32(head, 44)));
            offset = Align(offset + size, Alignment);
        }
        return records;
    }

    public static async Task<FDataExtractResult> ExtractAsync(string path, string destination,
        IReadOnlyDictionary<uint, string>? knownExtensions = null,
        IProgress<(int Done, int Total, string Name)>? progress = null,
        CancellationToken token = default,
        IReadOnlyDictionary<uint, ResourceOutputName>? outputNames = null,
        IReadOnlyDictionary<uint, string>? knownRelativePaths = null,
        string? manifestDirectory = null,
        IReadOnlyDictionary<string, string>? contentNames = null,
        Func<FDataRecord, bool>? shouldReadRecord = null,
        Func<FDataRecord, string?, bool>? includeRecord = null,
        string? manifestFileName = null,
        int manifestPolicyVersion = 1)
    {
        var records = ReadIndex(path);
        var selectedRecordCount = 0;
        Directory.CreateDirectory(destination);
        var files = new List<FDataExtracted>(); var errors = new List<string>(); long totalBytes = 0;
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        for (var index = 0; index < records.Count; index++)
        {
            token.ThrowIfCancellationRequested(); var record = records[index];
            if (shouldReadRecord is not null && !shouldReadRecord(record))
            {
                progress?.Report((index + 1, records.Count, $"跳过非首选语言资源 {record.ResourceId:x8}"));
                continue;
            }
            try
            {
                if (record.RecordSize > int.MaxValue) throw new InvalidDataException("单条 IDRK 记录超过 2 GB，当前版本拒绝整块载入");
                var bytes = new byte[(int)record.RecordSize]; source.Position = record.Offset;
                await source.ReadExactlyAsync(bytes, token);
                var content = DecompressRecord(bytes, record.UncompressedSize);
                var extension = DetectExtension(content, knownExtensions?.GetValueOrDefault(record.ResourceId));
                var outputName = outputNames?.GetValueOrDefault(record.ResourceId);
                var contentHash = contentNames is null ? null :
                    Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                if (includeRecord is not null && !includeRecord(record, contentHash))
                {
                    progress?.Report((index + 1, records.Count, $"跳过重复资源 {record.ResourceId:x8}"));
                    continue;
                }
                selectedRecordCount++;
                var knownPath = contentHash is not null && contentNames!.TryGetValue(contentHash, out var criticalPath)
                    ? criticalPath : knownRelativePaths?.GetValueOrDefault(record.ResourceId);
                var relativeDirectory = outputName?.RelativeDirectory ?? (knownPath is not null
                    ? Path.GetDirectoryName(knownPath) ?? "Root"
                    : Path.Combine("_unresolved", CategoryFor(extension), extension.TrimStart('.')));
                var directory = Path.Combine(destination, relativeDirectory);
                Directory.CreateDirectory(directory);
                var fileName = outputName is not null ? outputName.FileStem + extension
                    : knownPath is not null ? Path.GetFileName(knownPath)
                    : $"{record.ResourceId:x8}{extension}";
                var target = Path.Combine(directory, fileName);
                var sha = contentHash ?? Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                var targetMatches = false;
                if (File.Exists(target) && new FileInfo(target).Length == content.LongLength)
                {
                    await using var existing = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read,
                        1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    targetMatches = Convert.ToHexString(await SHA256.HashDataAsync(existing, token))
                        .Equals(sha, StringComparison.OrdinalIgnoreCase);
                }
                if (!targetMatches)
                    await File.WriteAllBytesAsync(target, content, token);
                files.Add(new(record.ResourceId, extension, content.LongLength, target, sha, record.Offset));
                totalBytes += content.LongLength;
                progress?.Report((index + 1, records.Count, Path.GetFileName(target)));
            }
            catch (Exception ex)
            {
                errors.Add($"0x{record.Offset:X} / {record.ResourceId:x8}: {ex.Message}");
                progress?.Report((index + 1, records.Count, $"错误 {record.ResourceId:x8}"));
            }
        }

        manifestDirectory ??= destination;
        Directory.CreateDirectory(manifestDirectory);
        manifestFileName ??= Path.GetFileNameWithoutExtension(path) + ".manifest.json";
        var manifestPath = Path.Combine(manifestDirectory, manifestFileName);
        var sourceInfo = new FileInfo(path);
        var manifest = new
        {
            Source = Path.GetFullPath(path),
            SourceLength = sourceInfo.Length,
            SourceLastWriteTimeUtc = sourceInfo.LastWriteTimeUtc,
            PolicyVersion = manifestPolicyVersion,
            Format = "PDRK/IDRK0000",
            Records = records.Count,
            SelectedRecords = selectedRecordCount,
            Extracted = files.Count,
            OutputBytes = totalBytes,
            Files = files.Select(x => new { Id = $"0x{x.ResourceId:x8}", x.Extension, x.Size, RelativePath = Path.GetRelativePath(destination, x.Path), x.Sha256, SourceOffset = $"0x{x.SourceOffset:X}" }),
            Errors = errors,
            Complete = errors.Count == 0 && files.Count == selectedRecordCount
        };
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), token);
        return new(records.Count, files.Count, totalBytes, files, errors);
    }

    public sealed record ResourceOutputName(string RelativeDirectory, string FileStem, string OfficialName);

    public static Dictionary<uint, ResourceOutputName> BuildOutputNameMap(GameNameCatalog? catalog, string? gameRoot = null)
    {
        var result = new Dictionary<uint, ResourceOutputName>();
        if (catalog?.Bindings is null) return result;
        var hasGameAnimationCatalog = !string.IsNullOrWhiteSpace(gameRoot);
        foreach (var ownerGroup in catalog.Bindings.GroupBy(x => (x.OfficialName, x.ResourceKind)))
        {
            var ordered = ownerGroup.DistinctBy(x => x.ResourceId).OrderBy(x => x.ResourceId, StringComparer.OrdinalIgnoreCase).ToList();
            for (var index = 0; index < ordered.Count; index++)
            {
                var binding = ordered[index];
                if (hasGameAnimationCatalog && binding.ResourceKind == "动画") continue;
                if (binding.ResourceKind == "动画" && binding.RelativePath.StartsWith("Field_Common.character.level", StringComparison.OrdinalIgnoreCase)) continue;
                if (!uint.TryParse(binding.ResourceId, System.Globalization.NumberStyles.HexNumber, null, out var id) || result.ContainsKey(id)) continue;
                var section = SectionForOfficialName(binding.OfficialName);
                var kind = FolderForKind(binding.ResourceKind);
                var directory = Path.Combine(section, Sanitize(binding.OfficialName), kind);
                var suffix = ordered.Count > 1 ? $"_{index + 1:D3}" : "";
                var stem = Sanitize($"{binding.OfficialName}_{kind}{suffix}");
                result[id] = new(directory, stem, binding.OfficialName);
            }
        }
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            foreach (var pair in KidsAnimationCatalog.Build(gameRoot))
            {
                if (!uint.TryParse(pair.Key, System.Globalization.NumberStyles.HexNumber, null, out var id)) continue;
                var binding = pair.Value;
                var owner = binding.Owners.Count == 1 ? binding.Owners[0] : "Shared_Action_Table";
                var directory = Path.Combine("animation_game_catalog", Sanitize(binding.Category), Sanitize(owner));
                result[id] = new(directory, Sanitize($"{owner}_animation_{pair.Key}"), owner);
            }
        }
        return result;
    }

    public static void AddLogicalFallbackNames(Dictionary<uint, ResourceOutputName> outputNames,
        IReadOnlyDictionary<uint, string> knownRelativePaths)
    {
        var groups = knownRelativePaths
            .Where(x => !outputNames.ContainsKey(x.Key))
            .GroupBy(x => NormalizeLogicalDirectory(Path.GetDirectoryName(x.Value) ?? "Root"), StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var ordered = group.OrderBy(x => x.Value, StringComparer.OrdinalIgnoreCase).ToList();
            var isSharedAnimation = group.Key.StartsWith("Field_Common.character.level", StringComparison.OrdinalIgnoreCase) && ordered.All(x => Path.GetExtension(x.Value).Equals(".g1a", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(x.Value).Equals(".g2a", StringComparison.OrdinalIgnoreCase));
            var domain = Sanitize(group.Key.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "Root");
            for (var index = 0; index < ordered.Count; index++)
            {
                var item = ordered[index];
                var kind = Path.GetExtension(item.Value).TrimStart('.').ToLowerInvariant();
                var stem = isSharedAnimation ? $"shared_animation_{index + 1:D6}" : $"{domain}_{kind}_{index + 1:D6}";
                var directory = isSharedAnimation ? Path.Combine("animation", "shared") : Path.Combine("game_tree", group.Key);
                outputNames[item.Key] = new(directory, stem, isSharedAnimation ? "SharedAnimation" : "未恢复正式名称");
            }
        }
    }

    public static void AddUnresolvedFallbackNames(Dictionary<uint, ResourceOutputName> outputNames,
        IEnumerable<uint> resourceIds)
    {
        var index = 0;
        foreach (var id in resourceIds.Distinct().OrderBy(x => x))
        {
            if (outputNames.ContainsKey(id)) continue;
            index++;
            outputNames[id] = new(Path.Combine("_unresolved", "resource"),
                $"unresolved_resource_{index:D6}", "未恢复正式名称");
        }
    }

    private static string NormalizeLogicalDirectory(string directory) => directory
        .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
        .TrimStart(Path.DirectorySeparatorChar);

    private static string SectionForOfficialName(string name) => name.StartsWith("HW", StringComparison.OrdinalIgnoreCase)
        ? "item" : name.Length > 0 ? char.ToUpperInvariant(name[0]) switch
    {
        'H' => "character", 'W' => "item", 'S' => "stage", 'E' => "event", _ => "named"
    } : "named";

    private static string FolderForKind(string kind) => kind switch
    {
        "模型" => "model", "动画" => "animation", "骨骼" => "skeleton", "贴图" => "texture", _ => "resource"
    };

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public static Dictionary<uint, string> BuildKnownExtensionMap(string root)
    {
        var map = new Dictionary<uint, string>();
        if (!Directory.Exists(root)) return map;
        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) name = name[2..];
            if (uint.TryParse(name, System.Globalization.NumberStyles.HexNumber, null, out var id))
                map.TryAdd(id, Path.GetExtension(path).ToLowerInvariant());
        }
        return map;
    }

    public static Dictionary<uint, string> BuildKnownRelativePathMap(string root)
    {
        var map = new Dictionary<uint, string>();
        if (!Directory.Exists(root)) return map;
        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                 { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) name = name[2..];
            if (uint.TryParse(name, System.Globalization.NumberStyles.HexNumber, null, out var id))
                map.TryAdd(id, Path.GetRelativePath(root, path));
        }
        return map;
    }

    internal static byte[] DecompressRecord(byte[] record, long expectedSize)
    {
        if (expectedSize < 0 || expectedSize > int.MaxValue) throw new InvalidDataException("解压后资源超过 2 GB");
        if (expectedSize == 0) return [];
        var target = new byte[(int)expectedSize]; var written = 0; var search = 48;
        while (written < target.Length)
        {
            var candidate = FindZlibHeader(record, search);
            if (candidate < 0) break;
            try
            {
                using var input = new MemoryStream(record, candidate, record.Length - candidate, false);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress, false);
                var maxChunk = Math.Min(0x4000, target.Length - written);
                var chunk = new byte[maxChunk]; var count = 0;
                while (count < chunk.Length)
                {
                    var n = zlib.Read(chunk, count, chunk.Length - count); if (n == 0) break; count += n;
                }
                // A valid record block expands to 16 KiB, except the final block.
                if (count == 0 || (count < 0x4000 && written + count != target.Length)) { search = candidate + 2; continue; }
                Buffer.BlockCopy(chunk, 0, target, written, count); written += count;
                search = candidate + 2;
            }
            catch (InvalidDataException) { search = candidate + 2; }
        }
        if (written != target.Length)
        {
            // Some records are stored rather than compressed. Search for a known resource magic.
            var raw = FindKnownMagic(record, 48);
            if (raw >= 0 && raw + target.Length <= record.Length)
            {
                Buffer.BlockCopy(record, raw, target, 0, target.Length); written = target.Length;
            }
        }
        if (written != target.Length) throw new InvalidDataException($"Zlib 数据不完整：得到 {written:N0}，预期 {target.Length:N0}");
        return target;
    }

    private static string DetectExtension(ReadOnlySpan<byte> data, string? known)
    {
        if (data.Length >= 8)
        {
            var magic = Encoding.ASCII.GetString(data[..8]);
            if (magic.StartsWith("_M1G")) return ".g1m";
            if (magic.StartsWith("_A1G")) return ".g1a";
            if (magic.StartsWith("_A2G")) return ".g2a";
            if (magic.StartsWith("_S1G") || magic.StartsWith("_S2G")) return ".g1s";
            if (magic.StartsWith("GT1G")) return ".g1t";
            if (magic.StartsWith("BGIR")) return ".rigbin";
            if (magic.StartsWith("_DOK")) return ".kidsobjdb";
            if (magic.StartsWith("_OC1G")) return ".g1co";
            if (magic.StartsWith("RIFF")) return ".wav";
            if (magic.StartsWith("OggS")) return ".ogg";
            if (data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G') return ".png";
        }
        return string.IsNullOrWhiteSpace(known) ? ".bin" : known.StartsWith('.') ? known.ToLowerInvariant() : "." + known.ToLowerInvariant();
    }

    private static string CategoryFor(string extension) => extension switch
    {
        ".g1m" or ".g1mx" => "Model", ".g1a" or ".g2a" => "Animation", ".g1s" or ".rigbin" => "Skeleton",
        ".g1t" or ".png" => "Texture", ".g1co" or ".g1cox" => "Collision", _ => "Other"
    };

    private static int FindZlibHeader(byte[] data, int start)
    {
        for (var i = Math.Max(0, start); i + 1 < data.Length; i++)
        {
            if (data[i] != 0x78) continue;
            var header = (data[i] << 8) | data[i + 1];
            if (header % 31 == 0 && (data[i + 1] is 0x01 or 0x5E or 0x9C or 0xDA)) return i;
        }
        return -1;
    }

    private static int FindKnownMagic(byte[] data, int start)
    {
        string[] magics = ["_M1G", "_A1G", "_A2G", "_S1G", "_S2G", "GT1G", "BGIR", "_DOK", "_OC1G", "RIFF", "OggS"];
        for (var i = start; i + 4 <= data.Length; i++)
            foreach (var magic in magics)
                if (data.AsSpan(i, 4).SequenceEqual(Encoding.ASCII.GetBytes(magic))) return i;
        return -1;
    }

    private static long FindNextMagic(Stream stream, long start, params byte[][] magics)
    {
        var buffer = new byte[1024 * 1024 + 8]; long position = start; var carry = 0;
        while (position < stream.Length)
        {
            stream.Position = position; var read = stream.Read(buffer, carry, buffer.Length - carry); if (read == 0) break;
            var total = carry + read;
            for (var i = 0; i + 8 <= total; i += Alignment)
                if (magics.Any(m => buffer.AsSpan(i, 8).SequenceEqual(m))) return position - carry + i;
            carry = Math.Min(8, total); Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry); position += read;
        }
        return -1;
    }

    private static long Align(long value, int alignment) => (value + alignment - 1) & ~(alignment - 1L);
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    private static ulong U64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
}
