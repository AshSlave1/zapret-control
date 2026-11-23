using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ZapretControl.Core;

public enum GameFilterMode
{
    Disabled,
    All,
    Tcp,
    Udp
}

public enum CheckStatus
{
    Ok,
    Warn,
    Fail,
    Info
}

public enum LogLevel
{
    Info,
    Warn,
    Error,
    Success
}

public sealed class StrategyProfile : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public List<string> Arguments { get; init; } = new();
    public string DesyncSummary { get; init; } = "";
    public string PortsSummary { get; init; } = "";
    public string RawCommand { get; init; } = "";
    public bool IsKnownGood { get; set; }
    public bool IsRecommended { get; set; }
    public string? Warning { get; set; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
                return;

            _isFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteGlyph)));
        }
    }

    public string FavoriteGlyph => IsFavorite ? "★" : "☆";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string BadgeText => IsKnownGood ? "ПРОВЕРЕНА" : "НЕ ПРОВЕРЕНА";
    public string WarningText => Warning ?? "";
    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning);
    public string Tooltip =>
        $"{Name}\n\n{DesyncSummary}\n{PortsSummary}\n\n{FilePath}";
}

public sealed class CheckResult
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public CheckStatus Status { get; init; } = CheckStatus.Info;
    public long DurationMs { get; init; }
    public string DurationText => DurationMs > 0 ? $"{DurationMs} мс" : "";
}

public sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Message { get; init; } = "";
    public LogLevel Level { get; init; } = LogLevel.Info;
    public string TimeText => Time.ToString("HH:mm:ss");
    public string FullTimeText => Time.ToString("dd.MM HH:mm:ss");
}

public sealed class StatusEvent
{
    public DateTime Time { get; init; }
    public string State { get; init; } = "";
    public string Profile { get; init; } = "";
    public string Pid { get; init; } = "";
    public string Detail { get; init; } = "";
    public string TimeText => Time.ToString("dd.MM HH:mm:ss");
    public string Summary => string.IsNullOrWhiteSpace(Profile) ? State : $"{State} · {Profile}";
}

public sealed class WinwsProcessDetails
{
    public int Pid { get; init; }
    public int ParentPid { get; init; }
    public DateTime? StartTime { get; init; }
    public string WindowTitle { get; init; } = "";
    public string CommandLine { get; init; } = "";

    public string DisplayText
    {
        get
        {
            var lines = new List<string>
            {
                $"PID: {Pid}",
                $"Родитель: {(ParentPid > 0 ? ParentPid.ToString() : "—")}",
                $"Старт: {(StartTime is DateTime time ? time.ToString("dd.MM.yyyy HH:mm:ss") : "—")}",
                $"Заголовок: {(string.IsNullOrWhiteSpace(WindowTitle) ? "—" : WindowTitle)}",
                "Командная строка:",
                string.IsNullOrWhiteSpace(CommandLine) ? "—" : CommandLine
            };
            return string.Join(Environment.NewLine, lines);
        }
    }
}

public sealed class SpeedSample
{
    public DateTime Time { get; init; }
    public double CloudflareMbps { get; init; }
    public double YouTubeMbps { get; init; }
    public string TimeText => Time.ToString("dd.MM HH:mm");
}

public sealed class SiteCheckResult
{
    public string Domain { get; init; } = "";
    public string Url { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool Ok { get; init; }
    public bool CanAddDomain { get; init; }
}

public sealed class RunningInfo
{
    public bool IsRunning { get; init; }
    public int[] Pids { get; init; } = Array.Empty<int>();
    public int? ManagedPid { get; init; }
    public DateTime? StartTime { get; init; }
    public string WindowTitle { get; init; } = "";
    public string PidsText => Pids.Length == 0 ? "—" : string.Join(", ", Pids);
}

public sealed record OperationResult(bool Success, string Message)
{
    public static OperationResult Ok(string message) => new(true, message);
    public static OperationResult Fail(string message) => new(false, message);
}

public sealed class AppConfig
{
    public string ZapretPath { get; set; } = "";
    public string SelectedProfile { get; set; } = "general (ALT)";
    public bool AutoStartApp { get; set; }
    public bool UseWallpaperBackground { get; set; } = true;
    public bool Scanlines { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoLockControl { get; set; } = true;
    public bool LockOnMinimize { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public bool ScheduledSpeedTests { get; set; }
    public List<string> FavoriteProfiles { get; set; } = new();
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public List<string> KnownGoodProfiles { get; set; } = new() { "general (ALT)", "general (ALT5)" };
    public List<SpeedSample> SpeedHistory { get; set; } = new();
}

public sealed class ListFileInfo
{
    public string Title { get; init; } = "";
    public string Path { get; init; } = "";
    public string Description { get; init; } = "";
    public string Content { get; init; } = "";
    public int LineCount { get; init; }
}
