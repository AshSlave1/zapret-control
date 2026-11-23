using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace ZapretControl.Core;

public sealed class DiagnosticsService : IDisposable
{
    private readonly HttpClient _http;

    public DiagnosticsService()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            // youtu.be отвечает 303 на www.youtube.com, который в РФ может не
            // резолвиться. Нам достаточно самого ответа, поэтому не идём по
            // редиректам.
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretControl/1.1");
    }

    public async Task<List<CheckResult>> RunChecksAsync(
        ZapretEnvironment env,
        WinwsController controller,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CheckResult>();

        var running = controller.GetRunningInfo();
        results.Add(new CheckResult
        {
            Name = "Процесс winws.exe",
            Detail = running.IsRunning
                ? $"запущен, PID {running.PidsText}" +
                  (running.ManagedPid is null ? " (внешний)" : " (запущен приложением)") +
                  (running.Pids.Length > 1 ? $" · экземпляров: {running.Pids.Length}" : "")
                : "не запущен",
            Status = running.IsRunning
                ? (running.Pids.Length > 1 ? CheckStatus.Warn : CheckStatus.Ok)
                : CheckStatus.Warn
        });

        var winDivert = controller.GetWinDivertStatus();
        results.Add(new CheckResult
        {
            Name = "Служба WinDivert",
            Detail = winDivert.Detail,
            Status = winDivert.Running ? CheckStatus.Ok : CheckStatus.Warn
        });

        var timestamps = controller.GetTcpTimestamps();
        results.Add(new CheckResult
        {
            Name = "TCP timestamps",
            Detail = timestamps.Enabled switch
            {
                true => "включены",
                false => "выключены · ALT может деградировать",
                _ => timestamps.Raw
            },
            Status = timestamps.Enabled switch
            {
                true => CheckStatus.Ok,
                false => CheckStatus.Fail,
                _ => CheckStatus.Warn
            }
        });

        var conflicts = controller.GetConflictingProcesses();
        results.Add(new CheckResult
        {
            Name = "Конфликтующие процессы",
            Detail = conflicts.Count == 0 ? "не найдены" : string.Join(", ", conflicts),
            Status = conflicts.Count <= 1 ? CheckStatus.Ok : CheckStatus.Warn
        });

        results.Add(CheckZapretFiles(env));
        results.Add(CheckProfiles(env));
        results.Add(CheckNetworkConflicts());

        var targets = new (string Name, string Url)[]
        {
            ("Discord", "https://discord.com/api/v9/gateway"),
            ("YouTube (youtu.be)", "https://youtu.be/"),
            ("YouTube CDN", "https://i.ytimg.com/vi/dQw4w9WgXcQ/mqdefault.jpg"),
            ("Google", "https://www.google.com/generate_204"),
            ("Cloudflare", "https://www.cloudflare.com/cdn-cgi/trace")
        };

        var httpTasks = targets.Select(t => CheckHttpAsync(t.Name, t.Url, cancellationToken)).ToList();
        results.AddRange(await Task.WhenAll(httpTasks));

        foreach (var host in new[] { "discord.com", "youtu.be", "i.ytimg.com", "redirector.googlevideo.com", "www.google.com" })
            results.Add(await CheckDnsAsync(host, cancellationToken));

        // www.youtube.com в РФ часто отдаёт NXDOMAIN на уровне DNS. Браузер
        // обходит это через DoH, поэтому такой результат — предупреждение,
        // а не ошибка обхода.
        var youtubeDns = await CheckDnsAsync("www.youtube.com", cancellationToken);
        if (youtubeDns.Status == CheckStatus.Fail)
        {
            results.Add(new CheckResult
            {
                Name = youtubeDns.Name,
                Detail = "нет DNS-ответа (блокировка провайдера)",
                Status = CheckStatus.Warn,
                DurationMs = youtubeDns.DurationMs
            });
        }
        else
        {
            results.Add(youtubeDns);
        }

        return results;
    }

    public async Task<SiteCheckResult> CheckSiteAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        var raw = input.Trim();
        if (raw.Length == 0)
            return new SiteCheckResult { Summary = "Введи адрес сайта." };

        if (!raw.Contains("://", StringComparison.Ordinal))
            raw = "https://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return new SiteCheckResult
            {
                Summary = "Некорректный адрес.",
                Detail = "Пример: youtube.com или https://example.com"
            };
        }

        var domain = uri.Host;
        var canAdd = !IPAddress.TryParse(domain, out _) &&
                     domain.Contains('.') &&
                     !domain.Equals("localhost", StringComparison.OrdinalIgnoreCase);

        var dnsText = "DNS: таймаут";
        var dnsOk = false;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (addresses.Length > 0)
            {
                dnsOk = true;
                dnsText = $"DNS: {addresses[0]} ({addresses.Length})";
            }
            else
            {
                dnsText = "DNS: нет адресов";
            }
        }
        catch (Exception ex)
        {
            dnsText = ex is TaskCanceledException ? "DNS: таймаут" : "DNS: " + ex.Message;
        }

        var sw = Stopwatch.StartNew();
        var httpText = "HTTP: нет ответа";
        var httpOk = false;
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            sw.Stop();
            httpText = $"HTTP {(int)response.StatusCode}";
            httpOk = (int)response.StatusCode < 500;
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            httpText = "HTTP: таймаут";
        }
        catch (Exception ex)
        {
            sw.Stop();
            httpText = "HTTP: " + ex.Message;
        }

        var ok = httpOk || dnsOk;
        return new SiteCheckResult
        {
            Domain = domain,
            Url = uri.ToString(),
            Summary = ok ? "Доступен" : "Недоступен",
            Detail = $"{dnsText} · {httpText} · {sw.ElapsedMilliseconds} мс",
            Ok = ok,
            CanAddDomain = canAdd
        };
    }

    private static CheckResult CheckZapretFiles(ZapretEnvironment env)
    {
        if (!env.IsValid)
        {
            return new CheckResult
            {
                Name = "Файлы zapret",
                Detail = @"не найден bin\winws.exe",
                Status = CheckStatus.Fail
            };
        }

        var missing = new List<string>();
        if (!File.Exists(env.WinDivertDll))
            missing.Add("WinDivert.dll");

        var profileCount = 0;
        try
        {
            profileCount = Directory.EnumerateFiles(env.RootPath, "*.bat", SearchOption.TopDirectoryOnly)
                .Count(f => !Path.GetFileName(f).Equals("service.bat", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // Количество профилей не критично для проверки файлов.
        }

        var version = "—";
        try
        {
            version = FileVersionInfo.GetVersionInfo(env.WinwsExe).FileVersion ?? "—";
        }
        catch
        {
            // Версия может быть недоступна.
        }

        var detail = $"winws {version} · профилей: {profileCount}";
        if (missing.Count > 0)
            detail += " · нет: " + string.Join(", ", missing);

        return new CheckResult
        {
            Name = "Файлы zapret",
            Detail = detail,
            Status = missing.Count == 0 && profileCount > 0
                ? CheckStatus.Ok
                : CheckStatus.Warn
        };
    }

    private static CheckResult CheckProfiles(ZapretEnvironment env)
    {
        if (!env.IsValid)
        {
            return new CheckResult
            {
                Name = "Целостность профилей",
                Detail = "папка zapret недоступна",
                Status = CheckStatus.Fail
            };
        }

        List<StrategyProfile> profiles;
        try
        {
            profiles = StrategyParser.ParseAll(env, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            return new CheckResult
            {
                Name = "Целостность профилей",
                Detail = ex.Message,
                Status = CheckStatus.Warn
            };
        }

        if (profiles.Count == 0)
        {
            return new CheckResult
            {
                Name = "Целостность профилей",
                Detail = "не найдено ни одного .bat с winws.exe",
                Status = CheckStatus.Warn
            };
        }

        var missingLists = new List<string>();
        var duplicateArgs = new List<string>();
        var withoutDesync = 0;

        foreach (var profile in profiles)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasDesync = false;

            foreach (var argument in profile.Arguments)
            {
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                    continue;

                if (!seen.Add(argument))
                    duplicateArgs.Add($"{profile.Name}: {argument}");

                var equals = argument.IndexOf('=');
                var key = equals > 0 ? argument[..equals] : argument;
                if (key.Equals("--dpi-desync", StringComparison.OrdinalIgnoreCase))
                    hasDesync = true;

                if (key is not ("--hostlist" or "--hostlist-exclude" or "--ipset" or "--ipset-exclude"))
                    continue;

                if (equals < 0)
                {
                    missingLists.Add($"{profile.Name}: {key} без значения");
                    continue;
                }

                foreach (var part in argument[(equals + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var value = part.Trim();
                    if (value.Length == 0)
                        continue;

                    var path = Path.IsPathRooted(value)
                        ? value
                        : Path.Combine(env.RootPath, value);

                    if (!File.Exists(path))
                        missingLists.Add($"{profile.Name}: {value}");
                }
            }

            if (!hasDesync)
                withoutDesync++;
        }

        var parts = new List<string>
        {
            $"профилей: {profiles.Count}"
        };

        if (missingLists.Count > 0)
            parts.Add("нет списков: " + string.Join(", ", missingLists.Take(4)) +
                      (missingLists.Count > 4 ? $" +{missingLists.Count - 4}" : ""));
        else
            parts.Add("списки: OK");

        if (duplicateArgs.Count > 0)
            parts.Add("дубли: " + string.Join(", ", duplicateArgs.Take(3)) +
                      (duplicateArgs.Count > 3 ? $" +{duplicateArgs.Count - 3}" : ""));

        if (withoutDesync > 0)
            parts.Add("без desync: " + withoutDesync);

        return new CheckResult
        {
            Name = "Целостность профилей",
            Detail = string.Join(" · ", parts),
            Status = missingLists.Count == 0 && duplicateArgs.Count == 0
                ? CheckStatus.Ok
                : CheckStatus.Warn
        };
    }

    private static CheckResult CheckNetworkConflicts()
    {
        var vpnNames = new List<string>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                    continue;

                var name = adapter.Name + " " + adapter.Description;
                if (name.Contains("vpn", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("wireguard", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("openvpn", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("tap", StringComparison.OrdinalIgnoreCase))
                {
                    vpnNames.Add(adapter.Name);
                }
            }
        }
        catch
        {
            // Список адаптеров может быть недоступен.
        }

        var proxyEnabled = false;
        var proxyServer = "";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            proxyEnabled = key?.GetValue("ProxyEnable") is int enabled && enabled != 0;
            proxyServer = key?.GetValue("ProxyServer")?.ToString() ?? "";
        }
        catch
        {
            // Реестр только читается.
        }

        var envProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY") ??
                       Environment.GetEnvironmentVariable("HTTP_PROXY") ?? "";

        if (vpnNames.Count == 0 && !proxyEnabled && string.IsNullOrWhiteSpace(envProxy))
        {
            return new CheckResult
            {
                Name = "VPN и прокси",
                Detail = "не обнаружены",
                Status = CheckStatus.Ok
            };
        }

        var parts = new List<string>();
        if (vpnNames.Count > 0)
            parts.Add("VPN: " + string.Join(", ", vpnNames.Distinct(StringComparer.OrdinalIgnoreCase)));
        if (proxyEnabled)
            parts.Add("прокси: " + (string.IsNullOrWhiteSpace(proxyServer) ? "включён" : proxyServer));
        if (!string.IsNullOrWhiteSpace(envProxy))
            parts.Add("env: " + envProxy);

        return new CheckResult
        {
            Name = "VPN и прокси",
            Detail = string.Join(" · ", parts),
            Status = CheckStatus.Warn
        };
    }

    public async Task<SpeedSample> MeasureSpeedAsync(CancellationToken cancellationToken = default)
    {
        var cloudflareTask = MeasureDownloadAsync(
            "https://speed.cloudflare.com/__down?bytes=4000000",
            4_000_000,
            cancellationToken);

        var youtubeTask = MeasureYouTubeCdnAsync(cancellationToken);

        await Task.WhenAll(cloudflareTask, youtubeTask);

        return new SpeedSample
        {
            Time = DateTime.Now,
            CloudflareMbps = cloudflareTask.Result,
            YouTubeMbps = youtubeTask.Result
        };
    }

    // www.youtube.com в РФ часто не резолвится, а сам видеопоток идёт
    // с CDN. Поэтому меряем превью с i.ytimg.com: это тот же Google/YouTube
    // CDN, и он показывает именно вечернюю деградацию YouTube.
    private static readonly string[] YouTubeThumbIds =
    {
        "dQw4w9WgXcQ", "9bZkp7q19f0", "kJQP7kiw5Fk", "RgKAFK5djSk",
        "OPf0YbXqDm0", "fJ9rUzIMcZQ", "hTWKbfoikeg", "YQHsXMglC9A"
    };

    private async Task<double> MeasureYouTubeCdnAsync(CancellationToken cancellationToken)
    {
        const long targetBytes = 2_500_000;
        var timeLimit = TimeSpan.FromSeconds(10);
        var sw = Stopwatch.StartNew();
        long total = 0;

        try
        {
            for (var i = 0; total < targetBytes && sw.Elapsed < timeLimit; i++)
            {
                var id = YouTubeThumbIds[i % YouTubeThumbIds.Length];
                var url = $"https://i.ytimg.com/vi/{id}/maxresdefault.jpg?cb={Guid.NewGuid():N}";

                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    continue;

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[64 * 1024];

                while (total < targetBytes && sw.Elapsed < timeLimit)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read <= 0)
                        break;
                    total += read;
                }
            }
        }
        catch
        {
            // Если часть данных уже пришла, считаем скорость по ней.
        }

        sw.Stop();
        var seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        return total * 8.0 / 1_000_000.0 / seconds;
    }

    public static void AppendSpeedSample(SpeedSample sample)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.AppDataDir);
            var line = string.Join(';',
                sample.Time.ToString("O", CultureInfo.InvariantCulture),
                sample.CloudflareMbps.ToString("F2", CultureInfo.InvariantCulture),
                sample.YouTubeMbps.ToString("F2", CultureInfo.InvariantCulture));

            if (!File.Exists(ConfigService.SpeedHistoryPath))
                File.WriteAllText(ConfigService.SpeedHistoryPath, "timestamp;cloudflare_mbps;youtube_mbps\r\n");

            File.AppendAllText(ConfigService.SpeedHistoryPath, line + "\r\n");
        }
        catch
        {
            // История замеров не критична для работы приложения.
        }
    }

    public static ObservableCollection<SpeedSample> LoadSpeedHistory(int max = 64)
    {
        var result = new ObservableCollection<SpeedSample>();
        try
        {
            if (!File.Exists(ConfigService.SpeedHistoryPath))
                return result;

            foreach (var line in File.ReadLines(ConfigService.SpeedHistoryPath).Skip(1))
            {
                var parts = line.Split(';');
                if (parts.Length < 3)
                    continue;

                if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
                    continue;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cf))
                    continue;
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var yt))
                    continue;

                result.Add(new SpeedSample { Time = time, CloudflareMbps = cf, YouTubeMbps = yt });
            }
        }
        catch
        {
            // Пустая история лучше падения.
        }

        return new ObservableCollection<SpeedSample>(result.TakeLast(max));
    }

    private async Task<CheckResult> CheckHttpAsync(string name, string url, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            sw.Stop();
            return new CheckResult
            {
                Name = name,
                Detail = $"HTTP {(int)response.StatusCode} {response.StatusCode}",
                Status = CheckStatus.Ok,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            return new CheckResult
            {
                Name = name,
                Detail = "таймаут",
                Status = CheckStatus.Fail,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CheckResult
            {
                Name = name,
                Detail = ex.Message,
                Status = CheckStatus.Fail,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    private async Task<CheckResult> CheckDnsAsync(string host, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            sw.Stop();
            var first = addresses.FirstOrDefault()?.ToString() ?? "нет адресов";
            return new CheckResult
            {
                Name = "DNS " + host,
                Detail = $"{first} ({addresses.Length} адр.)",
                Status = addresses.Length > 0 ? CheckStatus.Ok : CheckStatus.Fail,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CheckResult
            {
                Name = "DNS " + host,
                Detail = ex is TaskCanceledException ? "таймаут" : ex.Message,
                Status = CheckStatus.Fail,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    private async Task<double> MeasureDownloadAsync(string url, int maxBytes, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[64 * 1024];
            var total = 0;

            while (total < maxBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes - total)), cancellationToken);
                if (read <= 0)
                    break;
                total += read;
            }

            sw.Stop();
            var seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            return total * 8.0 / 1_000_000.0 / seconds;
        }
        catch
        {
            sw.Stop();
            return 0;
        }
    }

    public void Dispose() => _http.Dispose();
}
