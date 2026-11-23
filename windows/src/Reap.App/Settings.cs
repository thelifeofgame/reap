using System.Text.Json;
using Microsoft.Win32;
using Reap.Core;

namespace Reap.App;

/// <summary>未打包的应用用不了 ApplicationData，设置存在 %LOCALAPPDATA%\Reap\settings.json。</summary>
public sealed class Settings
{
    public const int MinimumIdleLimit = 30;

    public List<AgentKind> EnabledAgents { get; set; } = [.. Enum.GetValues<AgentKind>()];
    public bool AutoEndIdle { get; set; }
    public int IdleLimitMinutes { get; set; } = 120;
    public bool LaunchedBefore { get; set; }

    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reap");
    private static readonly string File = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) ?? new Settings();
            settings.IdleLimitMinutes = Math.Max(MinimumIdleLimit, settings.IdleLimitMinutes);
            return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>登录时启动：写当前用户的 Run 注册表项，带 --background 只进托盘不弹窗。</summary>
    public static bool LaunchAtLogin
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Reap") is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("Reap", $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue("Reap", throwOnMissingValue: false);
        }
    }
}
