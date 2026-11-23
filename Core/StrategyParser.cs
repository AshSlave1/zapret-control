using System.IO;
using System.Text;

namespace ZapretControl.Core;

public static class StrategyParser
{
    private const string WinwsToken = "winws.exe";

    public static List<StrategyProfile> ParseAll(
        ZapretEnvironment env,
        IReadOnlyCollection<string> knownGood,
        Action<string, LogLevel>? log = null)
    {
        var result = new List<StrategyProfile>();
        if (!Directory.Exists(env.RootPath))
            return result;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(env.RootPath, "*.bat", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).Equals("service.bat", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Не удалось прочитать папку стратегий: {ex.Message}", LogLevel.Error);
            return result;
        }

        foreach (var file in files)
        {
            try
            {
                var profile = ParseFile(env, file, knownGood);
                if (profile is not null)
                    result.Add(profile);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Стратегия {Path.GetFileName(file)} пропущена: {ex.Message}", LogLevel.Warn);
            }
        }

        return result
            .OrderByDescending(p => p.IsRecommended)
            .ThenByDescending(p => p.IsKnownGood)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static StrategyProfile? ParseFile(
        ZapretEnvironment env,
        string filePath,
        IReadOnlyCollection<string> knownGood)
    {
        var lines = File.ReadAllLines(filePath, Encoding.UTF8);
        var command = ExtractWinwsCommand(lines);
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var expanded = env.ExpandVariables(command);
        var args = Tokenize(expanded);
        if (args.Count == 0)
            return null;

        var name = Path.GetFileNameWithoutExtension(filePath);
        var desync = args
            .Where(a => a.StartsWith("--dpi-desync=", StringComparison.OrdinalIgnoreCase))
            .Select(a => a["--dpi-desync=".Length..])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var wfTcp = args.FirstOrDefault(a => a.StartsWith("--wf-tcp=", StringComparison.OrdinalIgnoreCase));
        var wfUdp = args.FirstOrDefault(a => a.StartsWith("--wf-udp=", StringComparison.OrdinalIgnoreCase));

        var ports = new List<string>();
        if (wfTcp is not null) ports.Add("TCP " + Shorten(wfTcp["--wf-tcp=".Length..], 48));
        if (wfUdp is not null) ports.Add("UDP " + Shorten(wfUdp["--wf-udp=".Length..], 48));

        var isAlt5 = name.Contains("ALT5", StringComparison.OrdinalIgnoreCase);
        var profile = new StrategyProfile
        {
            Name = name,
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
            Arguments = args,
            RawCommand = expanded,
            DesyncSummary = desync.Count > 0 ? string.Join(" + ", desync) : "без dpi-desync",
            PortsSummary = string.Join(" · ", ports),
            IsKnownGood = knownGood.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)),
            IsRecommended = name.Equals("general (ALT)", StringComparison.OrdinalIgnoreCase),
            Warning = isAlt5
                ? "Возможны просадки скорости и ретрансмиссии."
                : null
        };

        return profile;
    }

    private static string? ExtractWinwsCommand(string[] lines)
    {
        var sb = new StringBuilder();
        var collecting = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (!collecting)
            {
                var idx = line.IndexOf(WinwsToken, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    continue;

                collecting = true;
                var after = line[(idx + WinwsToken.Length)..].TrimStart();
                if (after.StartsWith('"'))
                    after = after[1..];

                var continues = after.TrimEnd().EndsWith('^');
                if (continues)
                    after = after.TrimEnd()[..^1];

                sb.Append(after);
                if (!continues)
                    break;
                continue;
            }

            var cont = line.EndsWith('^');
            var part = cont ? line[..^1] : line;
            sb.Append(' ').Append(part);
            if (!cont)
                break;
        }

        return sb.ToString().Trim();
    }

    public static List<string> Tokenize(string command)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in command)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    private static string Shorten(string value, int max)
    {
        value = value.Trim();
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }
}
