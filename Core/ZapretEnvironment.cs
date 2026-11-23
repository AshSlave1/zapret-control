using System.IO;
using System.Text;

namespace ZapretControl.Core;

public sealed class ZapretEnvironment
{
    public ZapretEnvironment(string rootPath)
    {
        RootPath = string.IsNullOrWhiteSpace(rootPath) ? "" : rootPath.TrimEnd('\\', '/');
    }

    public string RootPath { get; }
    public string BinPath => Path.Combine(RootPath, "bin");
    public string ListsPath => Path.Combine(RootPath, "lists");
    public string UtilsPath => Path.Combine(RootPath, "utils");
    public string WinwsExe => Path.Combine(BinPath, "winws.exe");
    public string WinDivertSys => Path.Combine(BinPath, "WinDivert64.sys");
    public string WinDivertDll => Path.Combine(BinPath, "WinDivert.dll");

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(RootPath) &&
        File.Exists(WinwsExe) &&
        File.Exists(WinDivertSys);

    public GameFilterMode GameFilter
    {
        get
        {
            try
            {
                var flag = Path.Combine(UtilsPath, "game_filter.enabled");
                if (!File.Exists(flag))
                    return GameFilterMode.Disabled;

                var first = File.ReadLines(flag).FirstOrDefault()?.Trim().ToLowerInvariant();
                return first switch
                {
                    "all" => GameFilterMode.All,
                    "tcp" => GameFilterMode.Tcp,
                    "udp" => GameFilterMode.Udp,
                    _ => GameFilterMode.Disabled
                };
            }
            catch
            {
                return GameFilterMode.Disabled;
            }
        }
    }

    public string GameFilterDescription => GameFilter switch
    {
        GameFilterMode.All => "включён (TCP + UDP)",
        GameFilterMode.Tcp => "включён (TCP)",
        GameFilterMode.Udp => "включён (UDP)",
        _ => "выключен"
    };

    public string ExpandVariables(string command)
    {
        var tcp = GameFilter is GameFilterMode.All or GameFilterMode.Tcp ? "1024-65535" : "12";
        var udp = GameFilter is GameFilterMode.All or GameFilterMode.Udp ? "1024-65535" : "12";

        return command
            .Replace("%~dp0", RootPath + "\\", StringComparison.OrdinalIgnoreCase)
            .Replace("%BIN%", BinPath + "\\", StringComparison.OrdinalIgnoreCase)
            .Replace("%LISTS%", ListsPath + "\\", StringComparison.OrdinalIgnoreCase)
            .Replace("%UTILS%", UtilsPath + "\\", StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilterTCP%", tcp, StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilterUDP%", udp, StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilter%", GameFilter == GameFilterMode.Disabled ? "12" : "1024-65535", StringComparison.OrdinalIgnoreCase)
            .Replace("%CD%", RootPath, StringComparison.OrdinalIgnoreCase);
    }

    public static string ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
        }
        catch
        {
            return "";
        }
    }
}
