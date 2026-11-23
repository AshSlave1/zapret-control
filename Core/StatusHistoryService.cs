using System.Globalization;
using System.IO;
using System.Text;

namespace ZapretControl.Core;

public static class StatusHistoryService
{
    public static void Append(string state, string profile, string pid, string detail)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.AppDataDir);

            if (!File.Exists(ConfigService.StatusHistoryPath))
            {
                File.WriteAllText(
                    ConfigService.StatusHistoryPath,
                    "timestamp;state;profile;pid;detail\r\n",
                    new UTF8Encoding(true));
            }

            var line = string.Join(
                ';',
                DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
                Clean(state),
                Clean(profile),
                Clean(pid),
                Clean(detail));

            File.AppendAllText(
                ConfigService.StatusHistoryPath,
                line + "\r\n",
                new UTF8Encoding(false));
        }
        catch
        {
            // История статусов не должна мешать управлению обходом.
        }
    }

    public static List<StatusEvent> LoadRecent(int max = 40)
    {
        var result = new List<StatusEvent>();
        try
        {
            if (!File.Exists(ConfigService.StatusHistoryPath))
                return result;

            foreach (var line in File.ReadLines(ConfigService.StatusHistoryPath).Skip(1))
            {
                var parts = line.Split(';');
                if (parts.Length < 5)
                    continue;

                if (!DateTime.TryParse(
                        parts[0],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var time))
                    continue;

                result.Add(new StatusEvent
                {
                    Time = time,
                    State = parts[1],
                    Profile = parts[2],
                    Pid = parts[3],
                    Detail = parts[4]
                });
            }
        }
        catch
        {
            // Пустая история лучше падения.
        }

        return result.TakeLast(max).Reverse().ToList();
    }

    private static string Clean(string value)
        => value.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
