using System.IO;
using System.Text.Json;

namespace ZapretControl.Core;

public sealed class ConfigService
{
    private static bool IsPortable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"));

    public static string AppDataDir { get; } = IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "data")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZapretControl");

    public static string ConfigPath { get; } = Path.Combine(AppDataDir, "config.json");
    public static string LogDir { get; } = Path.Combine(AppDataDir, "logs");
    public static string LogPath { get; } = Path.Combine(LogDir, "app.log");
    public static string SpeedHistoryPath { get; } = Path.Combine(AppDataDir, "speed-history.csv");
    public static string StatusHistoryPath { get; } = Path.Combine(AppDataDir, "status-history.csv");
    public static string PortableFlagPath { get; } = Path.Combine(AppContext.BaseDirectory, "portable.flag");

    public static bool PortableMode => IsPortable;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static AppConfig Load()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            Directory.CreateDirectory(LogDir);

            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (cfg is not null)
                {
                    if (string.IsNullOrWhiteSpace(cfg.ZapretPath) || !Directory.Exists(cfg.ZapretPath))
                        cfg.ZapretPath = DetectZapretPath() ?? cfg.ZapretPath;
                    return cfg;
                }
            }
        }
        catch
        {
            // Битый конфиг не должен мешать запуску приложения.
        }

        var fresh = new AppConfig { ZapretPath = DetectZapretPath() ?? "" };
        Save(fresh);
        return fresh;
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Игнорируем ошибки записи конфига: приложение продолжает работать.
        }
    }

    /// <summary>
    /// Ищет папку zapret: известные расположения, затем любую папку на рабочем
    /// столе, внутри которой есть bin\winws.exe.
    /// </summary>
    public static string? DetectZapretPath()
    {
        var candidates = new List<string>();

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop))
        {
            candidates.Add(Path.Combine(desktop, "zapret"));
            candidates.Add(Path.Combine(desktop, "zapret-win-bundle"));
            candidates.Add(Path.Combine(desktop, "zapret-win-bundle-master"));
            try
            {
                candidates.AddRange(Directory.EnumerateDirectories(desktop));
            }
            catch
            {
                // Нет доступа к рабочему столу — не критично.
            }
        }

        candidates.Add(@"C:\zapret");
        candidates.Add(@"C:\Tools\zapret");
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "zapret"));

        foreach (var dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "bin", "winws.exe")))
                    return dir;
            }
            catch
            {
                // Пропускаем недоступные пути.
            }
        }

        return null;
    }
}
