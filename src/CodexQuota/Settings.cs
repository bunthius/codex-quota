using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexQuota;

public sealed class Settings
{
    public string WidgetMode { get; set; } = "automatic";
    public double? Left { get; set; }
    public double? Top { get; set; }
    public string? CodexPath { get; set; }
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuota");
    private static string FilePath => Path.Combine(DataDirectory, "settings.json");
    public static Settings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            if (settings.WidgetMode is not ("automatic" or "always" or "tray")) settings.WidgetMode = "automatic";
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}

public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue("CodexQuota") is string; }
    }
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("CodexQuota", $"\"{Environment.ProcessPath}\" --startup");
        else key.DeleteValue("CodexQuota", false);
    }
}
