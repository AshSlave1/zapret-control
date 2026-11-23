using System.Globalization;
using System.IO;
using System.Text;

namespace ZapretControl.Core;

public static class AppLog
{
    private static readonly object Gate = new();

    public static event Action<LogEntry>? EntryAdded;

    public static void Info(string message) => Write(message, LogLevel.Info);
    public static void Warn(string message) => Write(message, LogLevel.Warn);
    public static void Error(string message) => Write(message, LogLevel.Error);
    public static void Success(string message) => Write(message, LogLevel.Success);

    public static void Write(string message, LogLevel level)
    {
        var entry = new LogEntry
        {
            Time = DateTime.Now,
            Message = message,
            Level = level
        };

        try
        {
            Directory.CreateDirectory(ConfigService.LogDir);
            var line = $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{level.ToString().ToUpperInvariant()}] {message}{Environment.NewLine}";
            lock (Gate)
                File.AppendAllText(ConfigService.LogPath, line, Encoding.UTF8);
        }
        catch
        {
            // Лог не должен ломать приложение.
        }

        EntryAdded?.Invoke(entry);
    }

    public static List<LogEntry> LoadRecent(int max = 300)
    {
        var result = new List<LogEntry>();
        try
        {
            if (!File.Exists(ConfigService.LogPath))
                return result;

            foreach (var line in File.ReadLines(ConfigService.LogPath).TakeLast(max))
            {
                if (line.Length < 22)
                    continue;

                if (!DateTime.TryParseExact(
                        line[..19],
                        "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var time))
                    continue;

                var rest = line[20..];
                var level = LogLevel.Info;

                if (rest.StartsWith('['))
                {
                    var close = rest.IndexOf(']');
                    if (close > 1 &&
                        Enum.TryParse(rest[1..close], true, out LogLevel parsed))
                    {
                        level = parsed;
                    }

                    rest = close >= 0 ? rest[(close + 1)..].TrimStart() : rest;
                }

                result.Add(new LogEntry
                {
                    Time = time,
                    Message = rest,
                    Level = level
                });
            }
        }
        catch
        {
            // Чтение старого лога не должно мешать запуску.
        }

        return result;
    }
}
