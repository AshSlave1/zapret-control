using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ZapretControl.Core;

public sealed class ListsService
{
    public const string GeneralUser = "list-general-user.txt";
    public const string ExcludeUser = "list-exclude-user.txt";
    public const string IpSetExcludeUser = "ipset-exclude-user.txt";

    private static readonly Regex DomainRegex = new(
        @"^(?=.{1,253}$)([a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}$",
        RegexOptions.Compiled);

    public ListFileInfo Load(ZapretEnvironment env, string fileName, string title, string description)
    {
        var path = Path.Combine(env.ListsPath, fileName);
        var content = ZapretEnvironment.ReadText(path);
        return new ListFileInfo
        {
            Title = title,
            Path = path,
            Description = description,
            Content = content,
            LineCount = CountMeaningfulLines(content)
        };
    }

    public OperationResult Save(ZapretEnvironment env, string fileName, string content, bool ipSet)
    {
        var path = Path.Combine(env.ListsPath, fileName);
        var validation = ipSet ? ValidateIpSet(content) : ValidateDomains(content);
        if (!validation.Ok)
            return OperationResult.Fail(validation.Message);

        try
        {
            Directory.CreateDirectory(env.ListsPath);

            if (File.Exists(path))
                File.Copy(path, path + ".bak", overwrite: true);

            var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
            File.WriteAllText(path, normalized, new UTF8Encoding(false));
            return OperationResult.Ok($"Сохранено: {fileName} ({CountMeaningfulLines(content)} строк).");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Не удалось сохранить {fileName}: {ex.Message}");
        }
    }

    public static (bool Ok, string Message) ValidateDomains(string content)
    {
        var errors = new List<string>();
        var lineNumber = 0;

        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (!DomainRegex.IsMatch(line))
                errors.Add($"строка {lineNumber}: «{line}» — не похоже на домен");
        }

        return errors.Count == 0
            ? (true, "OK")
            : (false, "Проверь список:\n" + string.Join('\n', errors.Take(12)) +
                     (errors.Count > 12 ? $"\n…и ещё {errors.Count - 12}" : ""));
    }

    public static (bool Ok, string Message) ValidateIpSet(string content)
    {
        var errors = new List<string>();
        var lineNumber = 0;

        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var addressPart = line;
            var slash = line.IndexOf('/');
            if (slash >= 0)
                addressPart = line[..slash];

            if (!IPAddress.TryParse(addressPart, out _))
                errors.Add($"строка {lineNumber}: «{line}» — не IP и не CIDR");
            else if (slash >= 0 &&
                     (!int.TryParse(line[(slash + 1)..], out var prefix) || prefix is < 0 or > 32))
                errors.Add($"строка {lineNumber}: «{line}» — неверная маска подсети");
        }

        return errors.Count == 0
            ? (true, "OK")
            : (false, "Проверь IP-список:\n" + string.Join('\n', errors.Take(12)) +
                     (errors.Count > 12 ? $"\n…и ещё {errors.Count - 12}" : ""));
    }

    public static int CountMeaningfulLines(string content)
    {
        return content.Replace("\r\n", "\n")
            .Split('\n')
            .Count(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#'));
    }
}
