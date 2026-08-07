using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace DWOriginsAssetTool.Core;

/// <summary>Runs Blender's own FBX writer after importing our temporary glTF.</summary>
public static class BlenderFbxExporter
{
    public static void Export(G1MInfo model, G1AClip? clip, string destination, string name, string gameRoot, bool includeGeometry = true,
        ActionCameraClip? camera = null, IProgress<(string Stage, int Percent)>? progress = null,
        bool cameraForUnrealSequencer = false, double cameraAspectRatio = 1.5,
        GltfExporter.AccessoryExport? accessory = null)
    {
        progress?.Report(("正在查找 Blender", 2));
        var blender = FindBlender(gameRoot) ?? throw new InvalidOperationException(
            "未找到 Blender。请确认 Steam\\steamapps\\common\\Blender\\blender.exe 存在。导出需要 Blender 的官方 FBX 导出器。\n" +
            "当前已支持：模型 FBX、角色动画 FBX、模型＋动画预览 FBX。");
        var work = Path.Combine(Path.GetTempPath(), "dworigins-blender-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var gltf = Path.Combine(work, "source.gltf");
        var script = Path.Combine(work, "blender_export.py");
        try
        {
            ExtractBlenderScript(script);
            progress?.Report(("正在生成标准中间数据", 10));
            // glTF and Blender use meters.  Keep the camera in meters as well:
            // UE converts the FBX meter value to its native centimeters.  Writing
            // game centimeters as meters makes every camera position 100x too far.
            GltfExporter.Export(model, clip, gltf, name, includeGeometry, camera,
                0.01f, 1f, accessory);
            progress?.Report(("正在准备 Blender 导出", 25));
            string? cameraFovPath = null;
            if (camera is not null)
            {
                cameraFovPath = Path.Combine(work, "camera_fov.json");
                var frames = GltfCameraTimes(camera.Clip.Info.Duration).Select(time =>
                    ActionCameraCatalog.Sample(camera, time).FieldOfView).ToArray();
                var transforms = GltfCameraTimes(camera.Clip.Info.Duration).Select(time =>
                {
                    var frame = ActionCameraCatalog.Sample(camera, time);
                    return new
                    {
                        // Sequencer consumes camera transforms in UE's native
                        // centimeters.  Keep the authored game values here and
                        // declare the FBX scene itself as centimeters in Blender.
                        position = new[] { frame.Position.X, frame.Position.Y, frame.Position.Z },
                        forward = new[] { frame.Forward.X, frame.Forward.Y, frame.Forward.Z },
                        up = new[] { frame.Up.X, frame.Up.Y, frame.Up.Z }
                    };
                }).ToArray();
                File.WriteAllText(cameraFovPath, JsonSerializer.Serialize(new
                {
                    sampleRate = 60,
                    fieldOfViewDegrees = frames,
                    transforms,
                    aspectRatio = Math.Max(0.1, cameraAspectRatio),
                    sensorHeightMm = 24.0,
                    // Blender/FBX is authored in meters.  The Sequencer importer
                    // converts those values once to Unreal's native centimeters.
                    // Writing the game centimeter numbers as FBX meters made UE
                    // multiply both location and object scale by 100.
                    unitSystem = "meters",
                    cameraOnly = cameraForUnrealSequencer
                }));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
            // Do not synchronously drain two redirected pipes: Blender can write enough
            // diagnostics to stderr to block itself while the parent waits on stdout.
            var start = new ProcessStartInfo(blender) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = false };
            start.ArgumentList.Add("--background"); start.ArgumentList.Add("--factory-startup"); start.ArgumentList.Add("--python"); start.ArgumentList.Add(script);
            start.ArgumentList.Add("--"); start.ArgumentList.Add(gltf); start.ArgumentList.Add(destination);
            if (cameraFovPath is not null) start.ArgumentList.Add(cameraFovPath);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Blender");
            progress?.Report(("Blender 正在写入 FBX", 35));
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(destination) || new FileInfo(destination).Length < 1024)
                throw new InvalidOperationException($"Blender FBX 导出失败（退出码 {process.ExitCode}）。\n{stdout}");
            progress?.Report(("FBX 导出完成", 100));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    private static void ExtractBlenderScript(string destination)
    {
        var assembly = typeof(BlenderFbxExporter).Assembly;
        var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(x =>
            x.EndsWith(".Assets.blender_export.py", StringComparison.OrdinalIgnoreCase) ||
            x.EndsWith(".blender_export.py", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            throw new InvalidOperationException("程序内置的 Blender 导出组件缺失");
        using var source = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("无法读取程序内置的 Blender 导出组件");
        using var target = File.Create(destination);
        source.CopyTo(target);
    }

    private static IEnumerable<float> GltfCameraTimes(float duration)
    {
        var frames = Math.Max(1, (int)Math.Ceiling(duration * 60));
        for (var frame = 0; frame <= frames; frame++) yield return Math.Min(duration, frame / 60f);
    }

    public static string? FindBlender(string gameRoot)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(UserPreferences.BlenderPath))
            candidates.Add(UserPreferences.BlenderPath);
        var library = string.IsNullOrWhiteSpace(gameRoot) ? null : Directory.GetParent(gameRoot)?.FullName;
        if (!string.IsNullOrWhiteSpace(library)) candidates.Add(Path.Combine(library, "Blender", "blender.exe"));
        candidates.AddRange(new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 4.5\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 4.4\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 4.3\blender.exe"
        });
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.IsReady))
            candidates.Add(Path.Combine(drive.RootDirectory.FullName, "Steam", "steamapps", "common", "Blender", "blender.exe"));
        var foundation = @"C:\Program Files\Blender Foundation";
        if (Directory.Exists(foundation))
            candidates.AddRange(Directory.EnumerateDirectories(foundation, "Blender*")
                .Select(path => Path.Combine(path, "blender.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }
}
