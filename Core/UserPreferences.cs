using System.Text.Json;

namespace DWOriginsAssetTool.Core;

internal static class UserPreferences
{
    private sealed class PreferenceData
    {
        public bool HideStartupGuide { get; set; }
        public string BlenderPath { get; set; } = "";
    }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DWOriginsAssetTool",
        "preferences.json");

    public static bool HideStartupGuide
    {
        get => Read().HideStartupGuide;
    }

    public static string BlenderPath => Read().BlenderPath;

    public static void SetHideStartupGuide(bool value)
    {
        try
        {
            var data = Read();
            data.HideStartupGuide = value;
            Write(data);
        }
        catch
        {
            // A read-only user profile must not prevent the tool from starting.
        }
    }

    public static void SetBlenderPath(string value)
    {
        var data = Read();
        data.BlenderPath = value;
        Write(data);
    }

    private static PreferenceData Read()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new();
            return JsonSerializer.Deserialize<PreferenceData>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch { return new(); }
    }

    private static void Write(PreferenceData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(data,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
