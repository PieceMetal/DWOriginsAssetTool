using System.Text.Json;

namespace DWOriginsAssetTool.Core;

internal static class UserPreferences
{
    private sealed class PreferenceData
    {
        public bool HideStartupGuide { get; set; }
    }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DWOriginsAssetTool",
        "preferences.json");

    public static bool HideStartupGuide
    {
        get
        {
            try
            {
                if (!File.Exists(SettingsPath)) return false;
                return JsonSerializer.Deserialize<PreferenceData>(File.ReadAllText(SettingsPath))?.HideStartupGuide == true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SetHideStartupGuide(bool value)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(
                new PreferenceData { HideStartupGuide = value },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A read-only user profile must not prevent the tool from starting.
        }
    }
}
