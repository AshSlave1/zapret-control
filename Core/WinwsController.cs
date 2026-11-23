using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace ZapretControl.Core;

public sealed class WinwsController
{
    private readonly Action<string, LogLevel> _log;
    private readonly object _gate = new();
    private Process? _managed;
    private int? _managedPid;

    public WinwsController(Action<string, LogLevel> log)
    {
        _log = log;
    }

    public int? ManagedPid
    {
        get
        {
            lock (_gate)
                return _managedPid;
        }
    }

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public RunningInfo GetRunningInfo()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("winws");
        }
        catch
        {
            processes = Array.Empty<Process>();
        }

        var pids = processes.Select(p => p.Id).OrderBy(id => id).ToArray();
        DateTime? startTime = null;
        var windowTitle = "";

        if (processes.Length > 0)
        {
            try
            {
                startTime = processes.Min(p => p.StartTime);
            }
            catch
            {
                // Нет прав на чтение StartTime — не критично.
            }

            try
            {
                // У winws.exe заголовок консоли обычно выглядит как
                // «zapret: general (ALT)» — по нему можно определить
                // стратегию внешнего процесса, ничего не трогая.
                windowTitle = processes
                    .Select(p => p.MainWindowTitle)
                    .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
            }
            catch
            {
                // Заголовок может быть недоступен без прав — не критично.
            }
        }

        foreach (var process in processes)
            process.Dispose();

        int? managedPid = null;
        lock (_gate)
        {
            if (_managed is { HasExited: false })
                managedPid = _managed.Id;
            else
                _managedPid = null;
        }

        return new RunningInfo
        {
            IsRunning = pids.Length > 0,
            Pids = pids,
            ManagedPid = managedPid,
            StartTime = startTime,
            WindowTitle = windowTitle
        };
    }

    public WinwsProcessDetails? GetProcessDetails(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.Equals("winws", StringComparison.OrdinalIgnoreCase))
                return null;

            DateTime? startTime = null;
            try
            {
                startTime = process.StartTime;
            }
            catch
            {
                // Нет прав на чтение StartTime — не критично.
            }

            var title = "";
            try
            {
                title = process.MainWindowTitle;
            }
            catch
            {
                // Заголовок может быть недоступен.
            }

            var commandLine = "";
            var parentPid = 0;

            var wmic = Path.Combine(Environment.SystemDirectory, "wbem", "wmic.exe");
            if (!File.Exists(wmic))
                wmic = "wmic";

            var result = RunCapture(
                wmic,
                $"process where processid={pid} get CommandLine,ParentProcessId /format:list");

            if (result.ExitCode == 0)
            {
                foreach (var raw in result.Output.Split('\n'))
                {
                    var line = raw.Trim();
                    var equals = line.IndexOf('=');
                    if (equals <= 0)
                        continue;

                    var key = line[..equals].Trim();
                    var value = line[(equals + 1)..].Trim();

                    if (key.Equals("CommandLine", StringComparison.OrdinalIgnoreCase))
                        commandLine = value;
                    else if (key.Equals("ParentProcessId", StringComparison.OrdinalIgnoreCase) &&
                             int.TryParse(value, out var parsedParent))
                        parentPid = parsedParent;
                }
            }

            return new WinwsProcessDetails
            {
                Pid = pid,
                ParentPid = parentPid,
                StartTime = startTime,
                WindowTitle = title,
                CommandLine = commandLine
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<OperationResult> StartAsync(ZapretEnvironment env, StrategyProfile profile)
    {
        if (!env.IsValid)
            return OperationResult.Fail("Папка zapret не найдена или в ней нет bin\\winws.exe.");

        var running = GetRunningInfo();
        if (running.IsRunning)
        {
            return OperationResult.Fail(
                $"winws.exe уже запущен (PID {running.PidsText}). " +
                "Останови его вручную или разблокируй управление в настройках.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = env.WinwsExe,
            WorkingDirectory = env.BinPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in profile.Arguments)
            psi.ArgumentList.Add(argument);

        var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _log("[winws] " + e.Data, LogLevel.Info);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;

            stderr.AppendLine(e.Data);
            _log("[winws] " + e.Data, LogLevel.Warn);
        };

        process.Exited += (_, _) =>
        {
            _log($"winws.exe завершился (PID {process.Id}, код {SafeExitCode(process)}).", LogLevel.Warn);
            lock (_gate)
            {
                if (_managedPid == process.Id)
                {
                    _managedPid = null;
                    _managed = null;
                }
            }
        };

        try
        {
            if (!process.Start())
                return OperationResult.Fail("Не удалось запустить winws.exe.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await Task.Delay(1300);

            if (process.HasExited)
            {
                var error = stderr.ToString().Trim();
                var suffix = string.IsNullOrWhiteSpace(error) ? "" : $"\n{error}";
                return OperationResult.Fail(
                    $"winws.exe завершился сразу после запуска (код {process.ExitCode}).{suffix}");
            }

            lock (_gate)
            {
                _managed = process;
                _managedPid = process.Id;
            }

            _log($"Запущена стратегия «{profile.Name}» (PID {process.Id}).", LogLevel.Success);
            return OperationResult.Ok($"Обход запущен: {profile.Name} (PID {process.Id}).");
        }
        catch (Exception ex)
        {
            process.Dispose();
            return OperationResult.Fail(
                "Не удалось запустить winws.exe. Проверь права администратора.\n" + ex.Message);
        }
    }

    public async Task<OperationResult> StopManagedAsync()
    {
        Process? process;
        lock (_gate)
            process = _managed;

        if (process is null)
            return OperationResult.Fail("Этот процесс запущен не приложением. Остановка внешнего процесса заблокирована.");

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }

            _log("Обход остановлен приложением.", LogLevel.Success);
            return OperationResult.Ok("Обход остановлен.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Не удалось остановить winws.exe: " + ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _managed = null;
                _managedPid = null;
            }
        }
    }

    public async Task<OperationResult> StopExternalAsync(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.Equals("winws", StringComparison.OrdinalIgnoreCase))
                return OperationResult.Fail($"Процесс PID {pid} — не winws.exe.");

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            _log($"Внешний процесс winws.exe (PID {pid}) остановлен после подтверждения.", LogLevel.Warn);
            return OperationResult.Ok($"Процесс PID {pid} остановлен.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Не удалось остановить PID {pid}: {ex.Message}");
        }
    }

    public (bool? Enabled, string Raw) GetTcpTimestamps()
    {
        var result = RunCapture("netsh", "interface tcp show global");
        if (result.ExitCode != 0)
            return (null, string.IsNullOrWhiteSpace(result.Output) ? "не удалось прочитать" : result.Output.Trim());

        foreach (var line in result.Output.Split('\n'))
        {
            var lower = line.ToLowerInvariant();
            if (!lower.Contains("timestamps") && !lower.Contains("метки времени"))
                continue;

            if (lower.Contains("enabled") || lower.Contains("включен"))
                return (true, line.Trim());
            if (lower.Contains("disabled") || lower.Contains("отключен"))
                return (false, line.Trim());
        }

        return (null, result.Output.Trim());
    }

    public (bool Running, string Detail) GetWinDivertStatus()
    {
        var states = new List<string>();
        var running = false;

        foreach (var service in new[] { "WinDivert", "WinDivert14" })
        {
            var result = RunCapture("sc", $"query {service}");
            if (result.ExitCode != 0)
                continue;

            // sc.exe локализован: "STATE" может быть "СОСТОЯНИЕ", поэтому ищем
            // само значение состояния, а не название поля.
            var match = Regex.Match(
                result.Output,
                @"\b(RUNNING|STOPPED|START_PENDING|STOP_PENDING|PAUSED|UNKNOWN)\b",
                RegexOptions.IgnoreCase);
            var state = match.Success ? match.Groups[1].Value.ToUpperInvariant() : "UNKNOWN";
            states.Add($"{service}: {state}");
            if (state == "RUNNING")
                running = true;
        }

        return states.Count == 0
            ? (false, "служба не установлена")
            : (running, string.Join(" · ", states));
    }

    public List<string> GetConflictingProcesses()
    {
        // Активный winws — это не конфликт, а сам обход. Ищем сторонние
        // DPI-инструменты, которые могут мешать друг другу.
        var names = new[] { "goodbyedpi", "GoodbyeDPI", "spoofdpi", "byedpi", "zapret" };
        var found = new List<string>();

        foreach (var name in names)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var process in processes)
                {
                    found.Add($"{process.ProcessName} (PID {process.Id})");
                    process.Dispose();
                }
            }
            catch
            {
                // Игнорируем.
            }
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool RelaunchElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) ||
                Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static (int ExitCode, string Output) RunCapture(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = GetConsoleEncoding(),
                StandardErrorEncoding = GetConsoleEncoding()
            };

            using var process = Process.Start(psi);
            if (process is null)
                return (-1, "");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(6000);

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
