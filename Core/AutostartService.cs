using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ZapretControl.Core;

public sealed class AutostartService
{
    public const string TaskName = "ZapretControl";

    public bool IsEnabled()
    {
        var result = Run("schtasks", new[] { "/Query", "/TN", TaskName });
        return result.ExitCode == 0;
    }

    public OperationResult Enable(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return OperationResult.Fail("Не удалось определить путь к приложению.");

        var result = Run("schtasks", new[]
        {
            "/Create",
            "/TN", TaskName,
            "/TR", $"\"{exePath}\"",
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/F"
        });

        return result.ExitCode == 0
            ? OperationResult.Ok("Автозапуск включён.")
            : OperationResult.Fail("Не удалось включить автозапуск. Нужны права администратора.\n" + result.Output.Trim());
    }

    public OperationResult Disable()
    {
        var result = Run("schtasks", new[] { "/Delete", "/TN", TaskName, "/F" });
        return result.ExitCode == 0
            ? OperationResult.Ok("Автозапуск выключен.")
            : OperationResult.Fail("Не удалось выключить автозапуск.\n" + result.Output.Trim());
    }

    private static (int ExitCode, string Output) Run(string fileName, string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = GetConsoleEncoding(),
                StandardErrorEncoding = GetConsoleEncoding()
            };

            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);
            if (process is null)
                return (-1, "");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(8000);

            return (process.ExitCode, string.Join('\n', new[] { output, error }.Where(s => !string.IsNullOrWhiteSpace(s))));
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static Encoding GetConsoleEncoding()
    {
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }
}
