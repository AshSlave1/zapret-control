using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ZapretControl.Core;

public sealed class BackupService
{
    private static readonly string[] ListFiles =
    {
        ListsService.GeneralUser,
        ListsService.ExcludeUser,
        ListsService.IpSetExcludeUser
    };

    public static string BackupDir => Path.Combine(ConfigService.AppDataDir, "backups");

    public string Create(ZapretEnvironment env)
    {
        Directory.CreateDirectory(BackupDir);

        var path = Path.Combine(
            BackupDir,
            $"zapret-control-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        AddFile(archive, ConfigService.ConfigPath, "config.json");
        AddFile(archive, ConfigService.SpeedHistoryPath, "speed-history.csv");

        if (env.IsValid)
        {
            foreach (var fileName in ListFiles)
                AddFile(archive, Path.Combine(env.ListsPath, fileName), "lists/" + fileName);
        }

        var manifest = archive.CreateEntry("manifest.txt");
        using var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false));
        writer.WriteLine("ZAPRET//CTRL backup");
        writer.WriteLine("Created: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        writer.WriteLine("Version: " + (typeof(BackupService).Assembly.GetName().Version?.ToString(3) ?? "1.1.0"));
        writer.WriteLine("Zapret: " + env.RootPath);

        return path;
    }

    public OperationResult Restore(string zipPath, ZapretEnvironment env)
    {
        if (!File.Exists(zipPath))
            return OperationResult.Fail("Архив не найден.");

        string? safetyBackup = null;
        try
        {
            safetyBackup = Create(env);
        }
        catch
        {
            // Если не удалось сделать страховочную копию, восстановление всё равно
            // возможно, но пользователь должен об этом знать.
        }

        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "ZapretControlRestore-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempDir);

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (string.IsNullOrWhiteSpace(name) || name.EndsWith('/'))
                        continue;

                    if (!IsAllowedEntry(name))
                        continue;

                    var target = Path.Combine(tempDir, name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }

            var configPath = Path.Combine(tempDir, "config.json");
            if (File.Exists(configPath))
            {
                try
                {
                    using var _ = JsonDocument.Parse(File.ReadAllText(configPath));
                }
                catch
                {
                    return OperationResult.Fail("В архиве повреждён config.json.");
                }

                CopyFile(configPath, ConfigService.ConfigPath);
            }

            CopyFile(
                Path.Combine(tempDir, "speed-history.csv"),
                ConfigService.SpeedHistoryPath);

            if (env.IsValid)
            {
                foreach (var fileName in ListFiles)
                {
                    var source = Path.Combine(tempDir, "lists", fileName);
                    if (!File.Exists(source))
                        continue;

                    var content = File.ReadAllText(source);
                    var valid = fileName == ListsService.IpSetExcludeUser
                        ? ListsService.ValidateIpSet(content).Ok
                        : ListsService.ValidateDomains(content).Ok;

                    if (!valid)
                        return OperationResult.Fail($"В архиве некорректный список {fileName}.");

                    var target = Path.Combine(env.ListsPath, fileName);
                    Directory.CreateDirectory(env.ListsPath);
                    if (File.Exists(target))
                        File.Copy(target, target + ".bak", overwrite: true);
                    CopyFile(source, target);
                }
            }

            var suffix = safetyBackup is null
                ? ""
                : $"\nСтраховочная копия: {safetyBackup}";
            return OperationResult.Ok("Настройки и списки восстановлены." + suffix);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Не удалось восстановить архив: " + ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Временная папка не критична.
            }
        }
    }

    private static bool IsAllowedEntry(string name)
    {
        if (name is "config.json" or "speed-history.csv" or "manifest.txt")
            return true;

        return ListFiles.Any(file =>
            name.Equals("lists/" + file, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddFile(ZipArchive archive, string path, string entryName)
    {
        if (!File.Exists(path))
            return;

        archive.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
    }

    private static void CopyFile(string source, string target)
    {
        if (!File.Exists(source))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }
}
