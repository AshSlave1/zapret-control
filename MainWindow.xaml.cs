using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using ZapretControl.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ZapretControl;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly Brush NeonBrush = MakeBrush(0xFF, 0x2D, 0x6F);
    private static readonly Brush MutedBrush = MakeBrush(0xA0, 0x8C, 0xA6);
    private static readonly Brush SuccessBrush = MakeBrush(0x39, 0xD9, 0x8A);
    private static readonly Brush DangerBrush = MakeBrush(0xFF, 0x3B, 0x5C);
    private static readonly Brush YellowBrush = MakeBrush(0xF2, 0xD0, 0x6B);
    private static readonly Brush CyanBrush = MakeBrush(0x00, 0xE5, 0xFF);
    private static readonly Brush NeonTint = MakeBrush(0x22, 0xFF, 0x2D, 0x6F);
    private static readonly Brush MutedTint = MakeBrush(0x22, 0xA0, 0x8C, 0xA6);
    private static readonly Brush GridBrush = MakeBrush(0x22, 0xFF, 0x2D, 0x6F);

    private readonly AppConfig _config;
    private ZapretEnvironment _env;
    private readonly WinwsController _winws;
    private readonly DiagnosticsService _diagnostics;
    private readonly ListsService _lists = new();
    private readonly AutostartService _autostart = new();
    private readonly DispatcherTimer _statusTimer;

    private readonly ObservableCollection<LogEntry> _logs = new();
    private readonly ObservableCollection<LogEntry> _allLogs = new();
    private readonly ObservableCollection<StatusEvent> _statusEvents = new();
    private readonly ObservableCollection<StrategyProfile> _profiles = new();
    private readonly ObservableCollection<CheckResult> _checks = new();
    private readonly ObservableCollection<SpeedSample> _speedHistory = new();
    private readonly ICollectionView _profilesView;
    private readonly ICollectionView _logView;
    private readonly BackupService _backup = new();
    private readonly DispatcherTimer _autoLockTimer;

    private Forms.NotifyIcon? _tray;
    private Drawing.Icon? _trayOn;
    private Drawing.Icon? _trayOff;
    private Forms.ToolStripMenuItem? _trayStatusItem;
    private Forms.ToolStripMenuItem? _trayToggleItem;
    private Forms.ToolStripMenuItem? _trayStopItem;
    private MonitorWindow? _monitor;

    private StrategyProfile? _selectedProfile;
    private bool _controlUnlocked;
    private bool _reallyExit;
    private bool _loading = true;
    private bool _trayBalloonShown;
    private bool _updatingAutostart;
    private string? _managedProfileName;
    private bool? _lastRunningState;
    private int _tickCounter;
    private DateTime? _unlockedAt;
    private DateTime _lastScheduledSpeed = DateTime.MinValue;
    private DateTime _toggleHoldStart;
    private bool _suppressToggleClick;
    private static readonly int[] ScheduledHours = { 20, 22, 0 };

    public MainWindow()
    {
        InitializeComponent();

        Icon = IconFactory.CreateImageSource(true, 64);

        _config = ConfigService.Load();
        ApplyWindowPlacement();
        _env = new ZapretEnvironment(_config.ZapretPath);
        _winws = new WinwsController(AppLog.Write);
        _diagnostics = new DiagnosticsService();
        _profilesView = CollectionViewSource.GetDefaultView(_profiles);
        _profilesView.Filter = FilterProfile;
        _logView = CollectionViewSource.GetDefaultView(_allLogs);
        _logView.Filter = FilterLog;

        foreach (var entry in AppLog.LoadRecent(60).AsEnumerable().Reverse())
            _logs.Add(entry);

        foreach (var entry in AppLog.LoadRecent(300).AsEnumerable().Reverse())
            _allLogs.Add(entry);

        foreach (var item in StatusHistoryService.LoadRecent(30))
            _statusEvents.Add(item);

        DataContext = this;

        AppLog.EntryAdded += OnLogEntry;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        StateChanged += MainWindow_StateChanged;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        _autoLockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoLockTimer.Tick += (_, _) => AutoLockTick();
        _autoLockTimer.Start();

        ApplyTheme();
        UpdateElevation();
        UpdateControlState();
        LoadProfiles();
        LoadLists();
        LoadSpeedHistory();
        CreateTray();

        ZapretPathBox.Text = _config.ZapretPath;
        TrayCheck.IsChecked = _config.MinimizeToTray;
        WallpaperCheck.IsChecked = _config.UseWallpaperBackground;
        ScanlinesCheck.IsChecked = _config.Scanlines;
        AutoLockCheck.IsChecked = _config.AutoLockControl;
        LockOnMinimizeCheck.IsChecked = _config.LockOnMinimize;
        NotificationsCheck.IsChecked = _config.NotificationsEnabled;
        ScheduledTestsCheck.IsChecked = _config.ScheduledSpeedTests;
        PortableCheck.IsChecked = ConfigService.PortableMode;

        _loading = false;
        RefreshStatus();

        AppLog.Info("Интерфейс загружен.");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ===================== COLLECTIONS =====================
    public ObservableCollection<LogEntry> Logs => _logs;
    public ObservableCollection<CheckResult> Checks => _checks;
    public ObservableCollection<StatusEvent> StatusEvents => _statusEvents;
    public ICollectionView ProfilesView => _profilesView;
    public ICollectionView LogView => _logView;

    // ===================== STATUS =====================
    private string _statusText = "ВЫКЛЮЧЕН";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private string _statusTitle = "ОБХОД ВЫКЛЮЧЕН";
    public string StatusTitle { get => _statusTitle; private set => Set(ref _statusTitle, value); }

    private string _statusBadgeText = "OFFLINE";
    public string StatusBadgeText { get => _statusBadgeText; private set => Set(ref _statusBadgeText, value); }

    private Brush _statusBrush = MutedBrush;
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }

    private Brush _statusBadgeBrush = MutedTint;
    public Brush StatusBadgeBrush { get => _statusBadgeBrush; private set => Set(ref _statusBadgeBrush, value); }

    private Color _statusColor = Color.FromRgb(0xA0, 0x8C, 0xA6);
    public Color StatusColor { get => _statusColor; private set => Set(ref _statusColor, value); }

    private string _toggleButtonText = "ВКЛЮЧИТЬ ОБХОД";
    public string ToggleButtonText { get => _toggleButtonText; private set => Set(ref _toggleButtonText, value); }

    private string _lockHint = "";
    public string LockHint { get => _lockHint; private set => Set(ref _lockHint, value); }

    private string _activeProfileName = "не выбрана";
    public string ActiveProfileName { get => _activeProfileName; private set => Set(ref _activeProfileName, value); }

    private string _activeProfileDesync = "—";
    public string ActiveProfileDesync { get => _activeProfileDesync; private set => Set(ref _activeProfileDesync, value); }

    private string _pidText = "—";
    public string PidText { get => _pidText; private set => Set(ref _pidText, value); }

    private string _uptimeText = "—";
    public string UptimeText { get => _uptimeText; private set => Set(ref _uptimeText, value); }

    private string _gameFilterText = "выключен";
    public string GameFilterText { get => _gameFilterText; private set => Set(ref _gameFilterText, value); }

    private string _winDivertStatus = "—";
    public string WinDivertStatus { get => _winDivertStatus; private set => Set(ref _winDivertStatus, value); }

    private string _winDivertDetail = "проверка…";
    public string WinDivertDetail { get => _winDivertDetail; private set => Set(ref _winDivertDetail, value); }

    private string _timestampsStatus = "—";
    public string TimestampsStatus { get => _timestampsStatus; private set => Set(ref _timestampsStatus, value); }

    private string _profilesCountText = "—";
    public string ProfilesCountText { get => _profilesCountText; private set => Set(ref _profilesCountText, value); }

    private string _knownGoodCountText = "";
    public string KnownGoodCountText { get => _knownGoodCountText; private set => Set(ref _knownGoodCountText, value); }

    private string _lastSpeedText = "Замеров нет.";
    public string LastSpeedText { get => _lastSpeedText; private set => Set(ref _lastSpeedText, value); }

    private string _speedHint = "";
    public string SpeedHint { get => _speedHint; private set => Set(ref _speedHint, value); }

    private string _clockText = "";
    public string ClockText { get => _clockText; private set => Set(ref _clockText, value); }

    private string _lastDiagnosticsText = "Проверки не запущены.";
    public string LastDiagnosticsText { get => _lastDiagnosticsText; private set => Set(ref _lastDiagnosticsText, value); }

    private string _zapretPathStatus = "";
    public string ZapretPathStatus { get => _zapretPathStatus; private set => Set(ref _zapretPathStatus, value); }

    private Brush _zapretPathStatusBrush = MutedBrush;
    public Brush ZapretPathStatusBrush { get => _zapretPathStatusBrush; private set => Set(ref _zapretPathStatusBrush, value); }

    private string _appVersion = "v1.1.0";
    public string AppVersion { get => _appVersion; private set => Set(ref _appVersion, value); }

    private string _siteCheckSummary = "";
    public string SiteCheckSummary { get => _siteCheckSummary; private set => Set(ref _siteCheckSummary, value); }

    private string _siteCheckDetail = "";
    public string SiteCheckDetail { get => _siteCheckDetail; private set => Set(ref _siteCheckDetail, value); }

    private Brush _siteCheckBrush = MutedBrush;
    public Brush SiteCheckBrush { get => _siteCheckBrush; private set => Set(ref _siteCheckBrush, value); }

    private bool _siteCheckCanAdd;
    public bool SiteCheckCanAdd { get => _siteCheckCanAdd; private set => Set(ref _siteCheckCanAdd, value); }

    private string _lastSiteDomain = "";

    private string _processDetailsText = "нет процесса";
    public string ProcessDetailsText { get => _processDetailsText; private set => Set(ref _processDetailsText, value); }

    private string _elevationText = "БЕЗ ПРАВ АДМИНИСТРАТОРА";
    public string ElevationText { get => _elevationText; private set => Set(ref _elevationText, value); }

    private Brush _elevationBrush = YellowBrush;
    public Brush ElevationBrush { get => _elevationBrush; private set => Set(ref _elevationBrush, value); }

    private string _controlStateText = "ЗАБЛОКИРОВАНО (БЕЗОПАСНЫЙ РЕЖИМ)";
    public string ControlStateText { get => _controlStateText; private set => Set(ref _controlStateText, value); }

    private Brush _controlStateBrush = SuccessBrush;
    public Brush ControlStateBrush { get => _controlStateBrush; private set => Set(ref _controlStateBrush, value); }

    private string _controlButtonText = "РАЗБЛОКИРОВАТЬ НА СЕАНС";
    public string ControlButtonText { get => _controlButtonText; private set => Set(ref _controlButtonText, value); }

    private bool _isBypassRunning;
    public bool IsBypassRunning { get => _isBypassRunning; private set => Set(ref _isBypassRunning, value); }

    private bool _isExternalProcess;
    public bool IsExternalProcess { get => _isExternalProcess; private set => Set(ref _isExternalProcess, value); }

    private bool _canToggle = true;
    public bool CanToggle { get => _canToggle; private set => Set(ref _canToggle, value); }

    private bool _canApplyProfile = true;
    public bool CanApplyProfile { get => _canApplyProfile; private set => Set(ref _canApplyProfile, value); }

    private bool _canStartProfile = true;
    public bool CanStartProfile { get => _canStartProfile; private set => Set(ref _canStartProfile, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
                UpdateCanExecute();
        }
    }

    private bool _isElevated;
    public bool IsElevated { get => _isElevated; private set => Set(ref _isElevated, value); }

    public bool IsControlLocked => !_controlUnlocked;

    public StrategyProfile? SelectedProfile
    {
        get => _selectedProfile;
        private set
        {
            if (!Set(ref _selectedProfile, value))
                return;

            if (!_loading)
            {
                _config.SelectedProfile = value?.Name ?? "";
                ConfigService.Save(_config);
            }

            if (!IsBypassRunning)
            {
                ActiveProfileName = value?.Name ?? "не выбрана";
                ActiveProfileDesync = value?.DesyncSummary ?? "—";
            }

            foreach (var profile in _profiles)
                profile.IsSelected = ReferenceEquals(profile, value);
        }
    }

    // ===================== LIFECYCLE =====================
    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            AutostartCheck.IsChecked = _autostart.IsEnabled();
        }
        catch
        {
            AutostartCheck.IsChecked = false;
        }

        DrawSpeedChart();
        RefreshSystemCards();

        if (!_env.IsValid)
            AppLog.Warn("Папка zapret не найдена. Укажи путь в настройках.");
    }

    private void ApplyWindowPlacement()
    {
        if (_config.WindowWidth is double width &&
            _config.WindowHeight is double height &&
            width >= MinWidth &&
            height >= MinHeight)
        {
            Width = width;
            Height = height;
        }

        if (_config.WindowLeft is double left && _config.WindowTop is double top)
        {
            var rect = new Rect(left, top, Width, Height);
            var virtualScreen = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);

            if (virtualScreen.IntersectsWith(rect))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (_config.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        try
        {
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;

            if (bounds.IsEmpty || bounds.Width < MinWidth || bounds.Height < MinHeight)
                return;

            _config.WindowLeft = bounds.Left;
            _config.WindowTop = bounds.Top;
            _config.WindowWidth = bounds.Width;
            _config.WindowHeight = bounds.Height;
            _config.WindowMaximized = WindowState == WindowState.Maximized;
            ConfigService.Save(_config);
        }
        catch
        {
            // Геометрия окна не критична для работы приложения.
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            RefreshStatus();
            RefreshSystemCards();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
        {
            NavList.SelectedIndex = 2;
            MainTabs.SelectedIndex = 2;
            e.Handled = true;
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_reallyExit)
            SaveWindowPlacement();

        if (_reallyExit)
            return;

        if (_config.MinimizeToTray)
        {
            if (_config.LockOnMinimize)
                LockControl("окно свёрнуто в трей");

            e.Cancel = true;
            Hide();

            if (!_trayBalloonShown && _tray is not null)
            {
                _tray.ShowBalloonTip(
                    2500,
                    "ZAPRET//CTRL",
                    "Свёрнуто в трей. Обход продолжает работать.",
                    Forms.ToolTipIcon.Info);
                _trayBalloonShown = true;
            }
        }
        else
        {
            _reallyExit = true;
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _config.LockOnMinimize)
            LockControl("окно свёрнуто");
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveWindowPlacement();
        _statusTimer.Stop();
        _autoLockTimer.Stop();
        AppLog.EntryAdded -= OnLogEntry;
        _diagnostics.Dispose();

        try
        {
            _monitor?.Close();
        }
        catch
        {
            // Мини-монитор мог быть уже закрыт.
        }

        _tray?.Dispose();
        _trayOn?.Dispose();
        _trayOff?.Dispose();
        base.OnClosed(e);

        if (_reallyExit)
            System.Windows.Application.Current.Shutdown();
    }

    // ===================== THEME / TRAY =====================
    private void ApplyTheme()
    {
        var wallpaper = WallpaperService.LoadWallpaper();
        if (_config.UseWallpaperBackground && wallpaper is not null)
        {
            WallpaperImage.Source = wallpaper;
            WallpaperImage.Visibility = Visibility.Visible;
            WallpaperImage.Opacity = 0.50;
            WallpaperImage.Effect = new BlurEffect
            {
                Radius = 28,
                RenderingBias = RenderingBias.Performance
            };
        }
        else
        {
            WallpaperImage.Visibility = Visibility.Collapsed;
            WallpaperImage.Effect = null;
        }

        ScanlinesLayer.Opacity = _config.Scanlines ? 0.05 : 0;
    }

    private void CreateTray()
    {
        _trayOn = IconFactory.CreateTrayIcon(true);
        _trayOff = IconFactory.CreateTrayIcon(false);

        _tray = new Forms.NotifyIcon
        {
            Icon = _trayOff,
            Text = "ZAPRET//CTRL — обход выключен",
            Visible = true
        };

        var menu = new Forms.ContextMenuStrip();

        _trayStatusItem = new Forms.ToolStripMenuItem("Статус: проверка…") { Enabled = false };

        var openItem = new Forms.ToolStripMenuItem("Открыть");
        openItem.Click += (_, _) => ShowFromTray();

        var diagnosticsItem = new Forms.ToolStripMenuItem("Диагностика");
        diagnosticsItem.Click += (_, _) =>
        {
            ShowFromTray();
            NavList.SelectedIndex = 2;
            MainTabs.SelectedIndex = 2;
        };

        var monitorItem = new Forms.ToolStripMenuItem("Мини-монитор");
        monitorItem.Click += (_, _) => ShowMonitor();

        var logItem = new Forms.ToolStripMenuItem("Журнал");
        logItem.Click += (_, _) =>
        {
            ShowFromTray();
            NavList.SelectedIndex = 4;
            MainTabs.SelectedIndex = 4;
        };

        var zapretFolderItem = new Forms.ToolStripMenuItem("Папка zapret");
        zapretFolderItem.Click += (_, _) => OpenFolder(_env.RootPath);

        _trayToggleItem = new Forms.ToolStripMenuItem("Включить обход");
        _trayToggleItem.Click += async (_, _) => await ToggleBypassAsync();

        _trayStopItem = new Forms.ToolStripMenuItem("Остановить обход");
        _trayStopItem.Click += async (_, _) =>
        {
            if (IsBypassRunning)
                await ToggleBypassAsync();
        };

        var exitItem = new Forms.ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => ExitApp();

        menu.Items.Add(_trayStatusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(openItem);
        menu.Items.Add(monitorItem);
        menu.Items.Add(diagnosticsItem);
        menu.Items.Add(logItem);
        menu.Items.Add(zapretFolderItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_trayToggleItem);
        menu.Items.Add(_trayStopItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void ShowMonitor()
    {
        if (_monitor is { IsVisible: true })
        {
            _monitor.Activate();
            return;
        }

        _monitor = new MonitorWindow
        {
            DataContext = this
        };

        var area = SystemParameters.WorkArea;
        _monitor.Left = Math.Max(area.Left + 12, area.Right - _monitor.Width - 24);
        _monitor.Top = area.Top + 24;
        _monitor.Closed += (_, _) => _monitor = null;
        _monitor.Show();
    }

    private void ExitApp()
    {
        _reallyExit = true;

        try
        {
            _monitor?.Close();
        }
        catch
        {
            // Мини-монитор мог быть уже закрыт.
        }

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        if (IsVisible)
            Close();
        else
            System.Windows.Application.Current.Shutdown();
    }

    // ===================== STATUS =====================
    private void RefreshStatus()
    {
        var info = _winws.GetRunningInfo();
        var running = info.IsRunning;
        var previousRunning = _lastRunningState;

        if (previousRunning != running)
        {
            _lastRunningState = running;
            AppLog.Info(running
                ? (info.ManagedPid is null
                    ? "Обнаружен внешний процесс winws.exe."
                    : "winws.exe запущен.")
                : "winws.exe не запущен.");
        }

        IsBypassRunning = running;
        IsExternalProcess = running && info.ManagedPid is null;
        ClockText = DateTime.Now.ToString("HH:mm:ss");
        GameFilterText = _env.GameFilterDescription;

        if (running)
        {
            StatusText = "АКТИВЕН";
            StatusTitle = "ОБХОД АКТИВЕН";
            StatusBadgeText = "LIVE";
            StatusBrush = NeonBrush;
            StatusBadgeBrush = NeonTint;
            StatusColor = Color.FromRgb(0xFF, 0x2D, 0x6F);
            ToggleButtonText = "ОСТАНОВИТЬ ОБХОД";
            PidText = info.PidsText;
            UptimeText = info.StartTime is DateTime start ? FormatUptime(DateTime.Now - start) : "—";

            if (info.ManagedPid is null)
            {
                var externalName = ExtractProfileName(info.WindowTitle);
                var externalProfile = FindProfile(externalName);

                ActiveProfileName = externalProfile?.Name ?? externalName ?? "Внешний процесс";
                ActiveProfileDesync = externalProfile?.DesyncSummary ?? "стратегия неизвестна";
            }
            else
            {
                var profile = FindProfile(_managedProfileName) ?? SelectedProfile;
                ActiveProfileName = profile?.Name ?? _managedProfileName ?? "—";
                ActiveProfileDesync = profile?.DesyncSummary ?? "—";
            }
        }
        else
        {
            StatusText = "ВЫКЛЮЧЕН";
            StatusTitle = "ОБХОД ВЫКЛЮЧЕН";
            StatusBadgeText = "OFFLINE";
            StatusBrush = MutedBrush;
            StatusBadgeBrush = MutedTint;
            StatusColor = Color.FromRgb(0xA0, 0x8C, 0xA6);
            ToggleButtonText = "ВКЛЮЧИТЬ ОБХОД";
            PidText = "—";
            UptimeText = "—";
            ActiveProfileName = SelectedProfile?.Name ?? "не выбрана";
            ActiveProfileDesync = SelectedProfile?.DesyncSummary ?? "—";
        }

        if (previousRunning != running)
            HandleStateChange(running, info, previousRunning);

        if (running && !_controlUnlocked)
            LockHint = "🔒 Управление заблокировано";
        else if (running && _controlUnlocked)
            LockHint = "⚠ Управление разблокировано";
        else if (!IsElevated)
            LockHint = "Нужны права администратора";
        else
            LockHint = "";

        UpdateCanExecute();
        UpdateTray(running, info.PidsText);
        CheckScheduledSpeedTests();

        if (++_tickCounter % 8 == 0)
            RefreshSystemCards();
    }

    private void HandleStateChange(bool running, RunningInfo info, bool? previousRunning)
    {
        var detail = previousRunning is null
            ? "старт приложения"
            : info.ManagedPid is null
                ? "внешний процесс"
                : "запущен приложением";

        StatusHistoryService.Append(
            running ? "RUNNING" : "STOPPED",
            ActiveProfileName,
            info.PidsText,
            detail);

        _statusEvents.Insert(0, new StatusEvent
        {
            Time = DateTime.Now,
            State = running ? "RUNNING" : "STOPPED",
            Profile = ActiveProfileName,
            Pid = info.PidsText,
            Detail = detail
        });

        while (_statusEvents.Count > 30)
            _statusEvents.RemoveAt(_statusEvents.Count - 1);

        if (!running)
            ProcessDetailsText = "winws.exe не запущен.";
        else if (previousRunning != true)
            ProcessDetailsText = "нажми «ДЕТАЛИ»";

        if (previousRunning is null)
            return;

        if (running)
        {
            Notify("Обход запущен", $"{ActiveProfileName} · PID {info.PidsText}", Forms.ToolTipIcon.Info);
        }
        else
        {
            Notify(
                "Обход остановлен",
                "winws.exe больше не запущен.",
                Forms.ToolTipIcon.Warning);
        }
    }

    private void UpdateCanExecute()
    {
        CanToggle = !IsBusy && (!IsBypassRunning || _controlUnlocked);
        CanApplyProfile = !IsBusy;
        CanStartProfile = !IsBusy && !IsBypassRunning;
    }

    private void UpdateTray(bool running, string pids)
    {
        if (_tray is null)
            return;

        _tray.Icon = running ? _trayOn : _trayOff;
        _tray.Text = running
            ? $"ZAPRET//CTRL — {ActiveProfileName} (PID {pids})"
            : "ZAPRET//CTRL — обход выключен";

        if (_trayStatusItem is not null)
            _trayStatusItem.Text = running
                ? $"Статус: {ActiveProfileName} · PID {pids}"
                : "Статус: обход выключен";

        if (_trayToggleItem is not null)
        {
            _trayToggleItem.Text = running ? "Остановить обход" : "Включить обход";
            _trayToggleItem.Enabled = running ? _controlUnlocked : true;
        }

        if (_trayStopItem is not null)
            _trayStopItem.Enabled = running && _controlUnlocked;
    }

    private void Notify(string title, string text, Forms.ToolTipIcon icon)
    {
        if (!_config.NotificationsEnabled || _tray is null)
            return;

        try
        {
            _tray.ShowBalloonTip(3000, title, text, icon);
        }
        catch
        {
            // Уведомление не критично.
        }
    }

    private void AutoLockTick()
    {
        if (!_controlUnlocked || !_config.AutoLockControl || _unlockedAt is null)
            return;

        if (DateTime.Now - _unlockedAt.Value >= TimeSpan.FromMinutes(10))
            LockControl("автоблокировка через 10 минут");
    }

    private void LockControl(string reason)
    {
        if (!_controlUnlocked)
            return;

        _controlUnlocked = false;
        _unlockedAt = null;
        AppLog.Info("Управление процессами заблокировано: " + reason + ".");
        UpdateControlState();
        RefreshStatus();
    }

    private async void CheckScheduledSpeedTests()
    {
        if (!_config.ScheduledSpeedTests || IsBusy)
            return;

        var now = DateTime.Now;
        if (Array.IndexOf(ScheduledHours, now.Hour) < 0)
            return;

        if (_lastScheduledSpeed.Date == now.Date && _lastScheduledSpeed.Hour == now.Hour)
            return;

        _lastScheduledSpeed = now;
        AppLog.Info($"Плановый замер скорости ({now:HH:00}).");
        await MeasureSpeedAsync(scheduled: true);
    }

    private void RefreshSystemCards()
    {
        var winDivert = _winws.GetWinDivertStatus();
        WinDivertStatus = winDivert.Running ? "РАБОТАЕТ" : "НЕ АКТИВЕН";
        WinDivertDetail = winDivert.Detail;

        var timestamps = _winws.GetTcpTimestamps();
        TimestampsStatus = timestamps.Enabled switch
        {
            true => "ВКЛЮЧЕНЫ",
            false => "ВЫКЛЮЧЕНЫ",
            _ => "НЕИЗВЕСТНО"
        };
    }

    private void UpdateElevation()
    {
        IsElevated = WinwsController.IsElevated;
        ElevationText = IsElevated ? "АДМИНИСТРАТОР" : "БЕЗ ПРАВ АДМИНИСТРАТОРА";
        ElevationBrush = IsElevated ? SuccessBrush : YellowBrush;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.0";
        AppVersion = $"v{version} · Windows 10";
    }

    private void UpdateControlState()
    {
        ControlStateText = _controlUnlocked
            ? "РАЗБЛОКИРОВАНО"
            : "ЗАБЛОКИРОВАНО";
        ControlStateBrush = _controlUnlocked ? DangerBrush : SuccessBrush;
        ControlButtonText = _controlUnlocked ? "ЗАБЛОКИРОВАТЬ" : "РАЗБЛОКИРОВАТЬ";
        OnPropertyChanged(nameof(IsControlLocked));
    }

    // ===================== DATA LOADING =====================
    private void LoadProfiles()
    {
        _profiles.Clear();
        var parsed = StrategyParser.ParseAll(_env, _config.KnownGoodProfiles, AppLog.Write);
        foreach (var profile in parsed)
        {
            profile.IsFavorite = _config.FavoriteProfiles.Any(name =>
                name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
            _profiles.Add(profile);
        }

        ProfilesCountText = $"{_profiles.Count} профилей";
        KnownGoodCountText = $"рабочих: {_profiles.Count(p => p.IsKnownGood)}";

        var selected = _profiles.FirstOrDefault(p => p.Name.Equals(_config.SelectedProfile, StringComparison.OrdinalIgnoreCase))
                       ?? _profiles.FirstOrDefault(p => p.IsRecommended)
                       ?? _profiles.FirstOrDefault();

        SelectedProfile = selected;
        UpdatePathStatus();

        if (_profiles.Count == 0)
            AppLog.Warn("Стратегии не найдены. Проверь путь к папке zapret.");
    }

    private void LoadLists()
    {
        if (!_env.IsValid)
        {
            GeneralListBox.Text = "";
            ExcludeListBox.Text = "";
            IpSetListBox.Text = "";
            GeneralListCount.Text = "папка zapret не найдена";
            ExcludeListCount.Text = "папка zapret не найдена";
            IpSetListCount.Text = "папка zapret не найдена";
            return;
        }

        var general = _lists.Load(_env, ListsService.GeneralUser, "list-general-user.txt", "");
        var exclude = _lists.Load(_env, ListsService.ExcludeUser, "list-exclude-user.txt", "");
        var ipset = _lists.Load(_env, ListsService.IpSetExcludeUser, "ipset-exclude-user.txt", "");

        GeneralListBox.Text = general.Content;
        ExcludeListBox.Text = exclude.Content;
        IpSetListBox.Text = ipset.Content;

        GeneralListCount.Text = LineCountText(general.LineCount);
        GeneralListCount.ToolTip = general.Path;
        ExcludeListCount.Text = LineCountText(exclude.LineCount);
        ExcludeListCount.ToolTip = exclude.Path;
        IpSetListCount.Text = LineCountText(ipset.LineCount);
        IpSetListCount.ToolTip = ipset.Path;
    }

    private void LoadSpeedHistory()
    {
        _speedHistory.Clear();
        foreach (var sample in DiagnosticsService.LoadSpeedHistory())
            _speedHistory.Add(sample);

        UpdateSpeedText();
    }

    private void UpdateSpeedText()
    {
        var last = _speedHistory.LastOrDefault();
        LastSpeedText = last is null
            ? "Замеров нет."
            : $"{last.Time:dd.MM HH:mm}{Environment.NewLine}Cloudflare {last.CloudflareMbps:F1} · YouTube CDN {last.YouTubeMbps:F1} Мбит/с";

        SpeedHint = last is not null && last.YouTubeMbps < 5 && last.CloudflareMbps > 10
            ? "Похоже на троттлинг CDN."
            : "";
    }

    private void UpdatePathStatus()
    {
        if (_env.IsValid)
        {
            ZapretPathStatus = "Папка найдена";
            ZapretPathStatusBrush = SuccessBrush;
        }
        else
        {
            ZapretPathStatus = @"Нет bin\winws.exe";
            ZapretPathStatusBrush = DangerBrush;
        }
    }

    // ===================== ACTIONS =====================
    private async Task ToggleBypassAsync()
    {
        if (IsBusy)
            return;

        if (IsBypassRunning)
        {
            if (!_controlUnlocked)
            {
                ShowLockedMessage();
                return;
            }

            var info = _winws.GetRunningInfo();
            if (info.Pids.Length == 0)
            {
                RefreshStatus();
                return;
            }

            var pid = info.ManagedPid ?? info.Pids[0];
            var question = info.ManagedPid is not null
                ? "Остановить обход?"
                : $"Остановить внешний процесс winws.exe (PID {pid})?\n\nЭто прервёт текущий обход.";

            if (MessageBox.Show(question, "ZAPRET//CTRL", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            IsBusy = true;
            var result = info.ManagedPid is not null
                ? await _winws.StopManagedAsync()
                : await _winws.StopExternalAsync(pid);
            IsBusy = false;

            AppLog.Write(result.Message, result.Success ? LogLevel.Success : LogLevel.Error);
            if (!result.Success)
                MessageBox.Show(result.Message, "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Error);

            _managedProfileName = null;
        }
        else
        {
            if (SelectedProfile is null)
            {
                MessageBox.Show("Сначала выбери стратегию на вкладке «Стратегии».",
                    "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!IsElevated)
            {
                AskRelaunch();
                return;
            }

            IsBusy = true;
            var result = await _winws.StartAsync(_env, SelectedProfile);
            IsBusy = false;

            AppLog.Write(result.Message, result.Success ? LogLevel.Success : LogLevel.Error);
            if (result.Success)
            {
                _managedProfileName = SelectedProfile.Name;
            }
            else
            {
                MessageBox.Show(result.Message, "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        RefreshStatus();
    }

    private async Task StartProfileAsync(StrategyProfile profile)
    {
        SelectedProfile = profile;

        if (IsBypassRunning)
        {
            MessageBox.Show("Обход уже запущен. Сначала останови текущий процесс, затем запусти новый.",
                "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ToggleBypassAsync();
    }

    private void ShowLockedMessage()
    {
        var result = MessageBox.Show(
            "Управление процессами заблокировано, чтобы не прервать текущий обход.\n\n" +
            "Разблокировать управление можно в настройках. Открыть настройки?",
            "ZAPRET//CTRL",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (result == MessageBoxResult.Yes)
        {
            NavList.SelectedIndex = 5;
            MainTabs.SelectedIndex = 5;
        }
    }

    private void AskRelaunch()
    {
        if (IsElevated)
            return;

        var result = MessageBox.Show(
            "Для запуска и остановки обхода нужны права администратора.\n\n" +
            "Перезапустить приложение от имени администратора?",
            "ZAPRET//CTRL",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        if (WinwsController.RelaunchElevated())
            ExitApp();
        else
            MessageBox.Show("Не удалось перезапустить приложение. Запусти его вручную от имени администратора.",
                "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void RunDiagnosticsAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        _checks.Clear();
        _checks.Add(new CheckResult
        {
            Name = "Диагностика",
            Detail = "Проверка…",
            Status = CheckStatus.Info
        });

        try
        {
            var results = await _diagnostics.RunChecksAsync(_env, _winws);
            _checks.Clear();
            foreach (var result in results)
                _checks.Add(result);

            LastDiagnosticsText = $"Проверено {results.Count} пунктов · {DateTime.Now:HH:mm:ss}";
            AppLog.Success("Диагностика завершена.");
        }
        catch (Exception ex)
        {
            _checks.Clear();
            _checks.Add(new CheckResult
            {
                Name = "Ошибка диагностики",
                Detail = ex.Message,
                Status = CheckStatus.Fail
            });
            AppLog.Error("Ошибка диагностики: " + ex);
        }
        finally
        {
            IsBusy = false;
            RefreshSystemCards();
        }
    }

    private async Task MeasureSpeedAsync(bool scheduled = false)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        SpeedHint = "Замер…";

        try
        {
            var sample = await _diagnostics.MeasureSpeedAsync();
            _speedHistory.Add(sample);
            while (_speedHistory.Count > 64)
                _speedHistory.RemoveAt(0);

            DiagnosticsService.AppendSpeedSample(sample);
            UpdateSpeedText();
            DrawSpeedChart();

            AppLog.Info($"Замер скорости: Cloudflare {sample.CloudflareMbps:F1} Мбит/с, YouTube CDN {sample.YouTubeMbps:F1} Мбит/с.");

            if (sample.CloudflareMbps <= 0 && sample.YouTubeMbps <= 0)
            {
                SpeedHint = "Не удалось замерить: таймаут или нет сети.";
            }
            else
            {
                SpeedHint = "";
                Notify(
                    scheduled ? "Плановый замер" : "Замер завершён",
                    $"Cloudflare {sample.CloudflareMbps:F1} · YouTube {sample.YouTubeMbps:F1} Мбит/с",
                    Forms.ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            SpeedHint = "Ошибка замера: " + ex.Message;
            AppLog.Error("Ошибка замера скорости: " + ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void DrawSpeedChart()
    {
        if (SpeedCanvas is null)
            return;

        SpeedCanvas.Children.Clear();

        var width = SpeedCanvas.ActualWidth;
        var height = SpeedCanvas.ActualHeight;
        if (width < 80 || height < 60)
            return;

        const double left = 40;
        const double right = 12;
        const double top = 10;
        const double bottom = 24;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        if (plotWidth <= 0 || plotHeight <= 0)
            return;

        var samples = _speedHistory.ToList();
        var max = samples.Count == 0
            ? 10
            : Math.Max(10, samples.Max(s => Math.Max(s.CloudflareMbps, s.YouTubeMbps)) * 1.15);

        for (var i = 0; i <= 4; i++)
        {
            var y = top + plotHeight * i / 4;
            var line = new Line
            {
                X1 = left,
                X2 = left + plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = GridBrush,
                StrokeThickness = 1
            };
            SpeedCanvas.Children.Add(line);

            var label = new TextBlock
            {
                Text = (max * (4 - i) / 4).ToString("F0"),
                FontSize = 9,
                FontFamily = new FontFamily("Consolas"),
                Foreground = MutedBrush
            };
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, y - 7);
            SpeedCanvas.Children.Add(label);
        }

        if (samples.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "Нет замеров",
                FontSize = 11,
                Width = plotWidth,
                TextAlignment = TextAlignment.Center,
                Foreground = MutedBrush
            };
            Canvas.SetLeft(empty, left);
            Canvas.SetTop(empty, top + plotHeight / 2 - 8);
            SpeedCanvas.Children.Add(empty);
            return;
        }

        var cloudflarePoints = new PointCollection();
        var youtubePoints = new PointCollection();

        for (var i = 0; i < samples.Count; i++)
        {
            var x = samples.Count == 1
                ? left + plotWidth / 2
                : left + plotWidth * i / (samples.Count - 1);

            var cfY = top + plotHeight - Math.Min(1, samples[i].CloudflareMbps / max) * plotHeight;
            var ytY = top + plotHeight - Math.Min(1, samples[i].YouTubeMbps / max) * plotHeight;

            cloudflarePoints.Add(new Point(x, cfY));
            youtubePoints.Add(new Point(x, ytY));
        }

        SpeedCanvas.Children.Add(new Polyline
        {
            Points = cloudflarePoints,
            Stroke = CyanBrush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        });

        SpeedCanvas.Children.Add(new Polyline
        {
            Points = youtubePoints,
            Stroke = NeonBrush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        });

        for (var i = 0; i < cloudflarePoints.Count; i++)
        {
            AddMarker(cloudflarePoints[i], CyanBrush);
            AddMarker(youtubePoints[i], NeonBrush);
        }

        var firstLabel = new TextBlock
        {
            Text = samples[0].TimeText,
            FontSize = 9,
            FontFamily = new FontFamily("Consolas"),
            Foreground = MutedBrush
        };
        Canvas.SetLeft(firstLabel, left);
        Canvas.SetTop(firstLabel, height - 18);
        SpeedCanvas.Children.Add(firstLabel);

        if (samples.Count > 1)
        {
            var lastLabel = new TextBlock
            {
                Text = samples[^1].TimeText,
                FontSize = 9,
                FontFamily = new FontFamily("Consolas"),
                Foreground = MutedBrush
            };
            Canvas.SetLeft(lastLabel, left + plotWidth - 62);
            Canvas.SetTop(lastLabel, height - 18);
            SpeedCanvas.Children.Add(lastLabel);
        }
    }

    private void AddMarker(Point point, Brush brush)
    {
        var marker = new Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = brush
        };
        Canvas.SetLeft(marker, point.X - 2);
        Canvas.SetTop(marker, point.Y - 2);
        SpeedCanvas.Children.Add(marker);
    }

    // ===================== EVENT HANDLERS =====================
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        try
        {
            DragMove();
        }
        catch
        {
            // DragMove может бросить исключение при отпущенной кнопке.
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabs is null || NavList.SelectedIndex < 0)
            return;

        MainTabs.SelectedIndex = NavList.SelectedIndex;
    }

    private void GoStrategies_Click(object sender, RoutedEventArgs e)
    {
        NavList.SelectedIndex = 1;
        MainTabs.SelectedIndex = 1;
    }

    private async void ToggleBypass_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleClick)
        {
            _suppressToggleClick = false;
            return;
        }

        await ToggleBypassAsync();
    }

    private async void StartProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StrategyProfile profile })
            await StartProfileAsync(profile);
    }

    private void SelectProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: StrategyProfile profile })
            return;

        SelectedProfile = profile;
        AppLog.Info($"Выбрана стратегия: {profile.Name}.");
        RefreshStatus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _profilesView?.Refresh();

    private bool FilterProfile(object obj)
    {
        if (obj is not StrategyProfile profile)
            return false;

        if (FavoritesOnlyCheck?.IsChecked == true && !profile.IsFavorite)
            return false;

        var query = SearchBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return profile.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               profile.DesyncSummary.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void RunDiagnostics_Click(object sender, RoutedEventArgs e) => RunDiagnosticsAsync();

    private void MeasureSpeed_Click(object sender, RoutedEventArgs e) => _ = MeasureSpeedAsync();

    private void SpeedCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawSpeedChart();

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить отчёт",
            Filter = "Текстовый файл (*.txt)|*.txt",
            FileName = $"zapret-report-{DateTime.Now:yyyyMMdd-HHmm}.txt"
        };

        if (dialog.ShowDialog() != true)
            return;

        var builder = new StringBuilder();
        builder.AppendLine("ZAPRET//CTRL — отчёт");
        builder.AppendLine("Дата: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine("Папка zapret: " + _env.RootPath);
        builder.AppendLine("Статус: " + StatusTitle);
        builder.AppendLine("Стратегия: " + ActiveProfileName);
        builder.AppendLine("PID: " + PidText);
        builder.AppendLine("WinDivert: " + WinDivertStatus + " (" + WinDivertDetail + ")");
        builder.AppendLine("TCP timestamps: " + TimestampsStatus);
        builder.AppendLine("Game filter: " + GameFilterText);
        builder.AppendLine();
        builder.AppendLine("ПРОВЕРКИ:");

        foreach (var check in _checks)
            builder.AppendLine($"- [{check.Status}] {check.Name}: {check.Detail} {check.DurationText}");

        builder.AppendLine();
        builder.AppendLine("ЗАМЕРЫ СКОРОСТИ:");
        foreach (var sample in _speedHistory)
            builder.AppendLine($"- {sample.Time:yyyy-MM-dd HH:mm} Cloudflare={sample.CloudflareMbps:F2} Мбит/с, YouTube CDN={sample.YouTubeMbps:F2} Мбит/с");

        try
        {
            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(false));
            MessageBox.Show("Отчёт сохранён:\n" + dialog.FileName, "ZAPRET//CTRL",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось сохранить отчёт: " + ex.Message, "ZAPRET//CTRL",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveGeneralList_Click(object sender, RoutedEventArgs e)
        => SaveList(ListsService.GeneralUser, GeneralListBox, GeneralListCount, ipSet: false);

    private void SaveExcludeList_Click(object sender, RoutedEventArgs e)
        => SaveList(ListsService.ExcludeUser, ExcludeListBox, ExcludeListCount, ipSet: false);

    private void SaveIpSetList_Click(object sender, RoutedEventArgs e)
        => SaveList(ListsService.IpSetExcludeUser, IpSetListBox, IpSetListCount, ipSet: true);

    private void SaveList(string fileName, TextBox box, TextBlock count, bool ipSet)
    {
        if (!_env.IsValid)
        {
            MessageBox.Show("Папка zapret не найдена. Проверь путь в настройках.",
                "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = _lists.Save(_env, fileName, box.Text, ipSet);
        AppLog.Write(result.Message, result.Success ? LogLevel.Success : LogLevel.Error);

        if (!result.Success)
        {
            MessageBox.Show(result.Message, "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var loaded = _lists.Load(_env, fileName, "", "");
        count.Text = LineCountText(loaded.LineCount);
        count.ToolTip = loaded.Path;
        MessageBox.Show("Сохранено. Применится после перезапуска обхода.",
            "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenListsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_env.ListsPath);

    private void ReloadProfiles_Click(object sender, RoutedEventArgs e)
    {
        var current = SelectedProfile?.Name;
        LoadProfiles();

        if (current is not null)
        {
            var again = FindProfile(current);
            if (again is not null)
                SelectedProfile = again;
        }

        RefreshStatus();
        AppLog.Info($"Список стратегий обновлён: {_profiles.Count} профилей.");
    }

    private void ReloadLists_Click(object sender, RoutedEventArgs e)
    {
        LoadLists();
        AppLog.Info("Списки перечитаны с диска.");
    }

    private void ClearSpeedHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_speedHistory.Count == 0)
        {
            MessageBox.Show("История замеров уже пуста.",
                "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            "Очистить историю замеров?\n\nФайл speed-history.csv будет удалён, график обнулится.",
            "ZAPRET//CTRL",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            if (File.Exists(ConfigService.SpeedHistoryPath))
                File.Delete(ConfigService.SpeedHistoryPath);

            _speedHistory.Clear();
            UpdateSpeedText();
            DrawSpeedChart();
            AppLog.Success("История замеров скорости очищена.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось очистить историю замеров: " + ex.Message);
            MessageBox.Show("Не удалось удалить файл истории:\n\n" + ex.Message,
                "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(ConfigService.LogPath))
            OpenPath(ConfigService.LogPath);
        else
            OpenFolder(ConfigService.LogDir);
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(ConfigService.LogDir);

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(ConfigService.AppDataDir);

    private void OpenZapretFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_env.RootPath);

    private void BrowseZapret_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Выбери папку zapret (в ней должна быть папка bin с winws.exe)",
            SelectedPath = _env.IsValid ? _env.RootPath : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            ZapretPathBox.Text = dialog.SelectedPath;
    }

    private void DetectZapret_Click(object sender, RoutedEventArgs e)
    {
        var path = ConfigService.DetectZapretPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show("Не удалось найти папку zapret автоматически. Укажи её вручную.",
                "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ZapretPathBox.Text = path;
    }

    private void ApplyPath_Click(object sender, RoutedEventArgs e)
    {
        var path = ZapretPathBox.Text.Trim();
        var env = new ZapretEnvironment(path);
        if (!env.IsValid)
        {
            MessageBox.Show(@"В этой папке нет bin\winws.exe или bin\WinDivert64.sys. Проверь путь.",
                "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _env = env;
        _config.ZapretPath = env.RootPath;
        ConfigService.Save(_config);

        LoadProfiles();
        LoadLists();
        UpdatePathStatus();
        RefreshStatus();

        AppLog.Success("Путь к zapret обновлён: " + env.RootPath);
        if (IsBypassRunning)
            MessageBox.Show("Путь обновлён. Изменения — после перезапуска обхода.",
                "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RelaunchAdmin_Click(object sender, RoutedEventArgs e) => AskRelaunch();

    private void UnlockControl_Click(object sender, RoutedEventArgs e)
    {
        if (_controlUnlocked)
        {
            _controlUnlocked = false;
            _unlockedAt = null;
            AppLog.Info("Управление процессами снова заблокировано.");
            UpdateControlState();
            RefreshStatus();
            return;
        }

        var result = MessageBox.Show(
            "Разблокировать управление процессами?\n\n" +
            "Это позволит приложению останавливать winws.exe. Если обход сейчас запущен, " +
            "кнопка «Остановить» сможет прервать его.\n\n" +
            "Разблокировка действует только до закрытия приложения.",
            "ZAPRET//CTRL",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        _controlUnlocked = true;
        _unlockedAt = DateTime.Now;
        AppLog.Warn("Управление процессами разблокировано на сеанс.");
        UpdateControlState();
        RefreshStatus();
    }

    private void AutostartCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingAutostart)
            return;

        var want = AutostartCheck.IsChecked == true;

        if (want)
        {
            var result = _autostart.Enable(GetExecutablePath());
            if (!result.Success)
            {
                _updatingAutostart = true;
                AutostartCheck.IsChecked = false;
                _updatingAutostart = false;
                AppLog.Error(result.Message);
                MessageBox.Show(result.Message, "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _config.AutoStartApp = true;
            ConfigService.Save(_config);
            AppLog.Success(result.Message);
        }
        else
        {
            var result = _autostart.Disable();
            if (!result.Success)
            {
                _updatingAutostart = true;
                AutostartCheck.IsChecked = true;
                _updatingAutostart = false;
                AppLog.Error(result.Message);
                MessageBox.Show(result.Message, "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _config.AutoStartApp = false;
            ConfigService.Save(_config);
            AppLog.Success(result.Message);
        }
    }

    private void WallpaperCheck_Click(object sender, RoutedEventArgs e)
    {
        _config.UseWallpaperBackground = WallpaperCheck.IsChecked == true;
        ConfigService.Save(_config);
        ApplyTheme();
    }

    private void ScanlinesCheck_Click(object sender, RoutedEventArgs e)
    {
        _config.Scanlines = ScanlinesCheck.IsChecked == true;
        ConfigService.Save(_config);
        ApplyTheme();
    }

    private void TrayCheck_Click(object sender, RoutedEventArgs e)
    {
        _config.MinimizeToTray = TrayCheck.IsChecked == true;
        ConfigService.Save(_config);
    }

    private void OpenMonitor_Click(object sender, RoutedEventArgs e) => ShowMonitor();

    private async void CheckSite_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
            return;

        var input = SiteCheckBox.Text.Trim();
        if (input.Length == 0)
        {
            SiteCheckSummary = "Введи адрес сайта.";
            SiteCheckDetail = "";
            SiteCheckBrush = YellowBrush;
            SiteCheckCanAdd = false;
            return;
        }

        IsBusy = true;
        SiteCheckSummary = "Проверка…";
        SiteCheckDetail = "";
        SiteCheckBrush = MutedBrush;
        SiteCheckCanAdd = false;

        try
        {
            var result = await _diagnostics.CheckSiteAsync(input);
            SiteCheckSummary = result.Summary;
            SiteCheckDetail = result.Detail;
            SiteCheckBrush = result.Ok ? SuccessBrush : DangerBrush;
            _lastSiteDomain = result.Domain;
            SiteCheckCanAdd = result.CanAddDomain;

            AppLog.Write(
                $"Проверка сайта {result.Domain}: {result.Summary} · {result.Detail}",
                result.Ok ? LogLevel.Success : LogLevel.Warn);
        }
        catch (Exception ex)
        {
            SiteCheckSummary = "Ошибка проверки.";
            SiteCheckDetail = ex.Message;
            SiteCheckBrush = DangerBrush;
            AppLog.Error("Ошибка проверки сайта: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddSiteToGeneral_Click(object sender, RoutedEventArgs e)
        => AddSiteToList(ListsService.GeneralUser, "общий список");

    private void AddSiteToExclude_Click(object sender, RoutedEventArgs e)
        => AddSiteToList(ListsService.ExcludeUser, "исключения");

    private void AddSiteToList(string fileName, string title)
    {
        if (!_env.IsValid)
        {
            MessageBox.Show("Папка zapret не найдена. Проверь путь в настройках.",
                "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_lastSiteDomain))
        {
            MessageBox.Show("Сначала проверь сайт.", "ZAPRET//CTRL",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var loaded = _lists.Load(_env, fileName, "", "");
        var exists = loaded.Content
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Any(line => line.Equals(_lastSiteDomain, StringComparison.OrdinalIgnoreCase));

        if (exists)
        {
            MessageBox.Show($"{_lastSiteDomain} уже есть в списке «{title}».",
                "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var content = loaded.Content.TrimEnd();
        if (content.Length > 0)
            content += Environment.NewLine;
        content += _lastSiteDomain;

        var result = _lists.Save(_env, fileName, content, ipSet: false);
        AppLog.Write(result.Message, result.Success ? LogLevel.Success : LogLevel.Error);

        if (!result.Success)
        {
            MessageBox.Show(result.Message, "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LoadLists();
        MessageBox.Show($"{_lastSiteDomain} добавлен в «{title}». Применится после перезапуска обхода.",
            "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void LoadProcessDetails_Click(object sender, RoutedEventArgs e)
    {
        var info = _winws.GetRunningInfo();
        if (!info.IsRunning)
        {
            ProcessDetailsText = "winws.exe не запущен.";
            return;
        }

        var pid = info.Pids.FirstOrDefault();
        var details = _winws.GetProcessDetails(pid);
        ProcessDetailsText = details?.DisplayText ?? "Не удалось прочитать детали процесса.";
    }

    private void CopyPid_Click(object sender, RoutedEventArgs e)
    {
        if (!IsBypassRunning)
        {
            MessageBox.Show("winws.exe не запущен.", "ZAPRET//CTRL",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CopyText(PidText);
    }

    private void OpenStatusHistory_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(ConfigService.StatusHistoryPath))
            OpenPath(ConfigService.StatusHistoryPath);
        else
            OpenFolder(ConfigService.AppDataDir);
    }

    private void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _backup.Create(_env);
            AppLog.Success("Создан бэкап: " + path);
            Notify("Бэкап создан", System.IO.Path.GetFileName(path), Forms.ToolTipIcon.Info);
            MessageBox.Show("Бэкап создан:\n" + path, "ZAPRET//CTRL",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось создать бэкап: " + ex.Message);
            MessageBox.Show("Не удалось создать бэкап:\n\n" + ex.Message, "ZAPRET//CTRL",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(BackupService.BackupDir);

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбрать бэкап",
            Filter = "Архив (*.zip)|*.zip",
            InitialDirectory = BackupService.BackupDir
        };

        if (dialog.ShowDialog() != true)
            return;

        var confirm = MessageBox.Show(
            "Восстановить настройки и списки из архива?\n\n" +
            "Текущие файлы будут заменены. Перед восстановлением создаётся страховочная копия.",
            "ZAPRET//CTRL",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        var result = _backup.Restore(dialog.FileName, _env);
        AppLog.Write(result.Message, result.Success ? LogLevel.Success : LogLevel.Error);

        if (!result.Success)
        {
            MessageBox.Show(result.Message, "ZAPRET//CTRL",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadLists();
        LoadProfiles();
        MessageBox.Show(
            result.Message + "\n\nДля применения config.json перезапусти приложение.",
            "ZAPRET//CTRL",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(BackupService.BackupDir);
        OpenFolder(BackupService.BackupDir);
    }

    private void AutoLockCheck_Click(object sender, RoutedEventArgs e)
    {
        _config.AutoLockControl = AutoLockCheck.IsChecked == true;
        ConfigService.Save(_config);
    }

    private void LockOnMinimizeCheck_Click(object sender, RoutedEventArgs e)
    {
        _config.LockOnMinimize = LockOnMinimizeCheck.IsChecked == true;
        ConfigService.Save(_config);
    }

    private void NotificationsCheck_Click(object sender, RoutedEventArgs e)
    {
        _config.NotificationsEnabled = NotificationsCheck.IsChecked == true;
        ConfigService.Save(_config);
    }

    private void ScheduledTestsCheck_Click(object sender, RoutedEventArgs e)
    {
        _config.ScheduledSpeedTests = ScheduledTestsCheck.IsChecked == true;
        ConfigService.Save(_config);
    }

    private void PortableCheck_Click(object sender, RoutedEventArgs e)
    {
        var want = PortableCheck.IsChecked == true;

        try
        {
            if (want)
                File.WriteAllText(ConfigService.PortableFlagPath, "portable");
            else if (File.Exists(ConfigService.PortableFlagPath))
                File.Delete(ConfigService.PortableFlagPath);

            AppLog.Info(want
                ? "Включён портативный режим. Требуется перезапуск."
                : "Портативный режим выключен. Требуется перезапуск.");

            MessageBox.Show(
                "Режим изменится после перезапуска приложения.\n\n" +
                (want
                    ? "Данные будут храниться в папке data рядом с exe."
                    : "Данные снова будут храниться в %LOCALAPPDATA%\\ZapretControl."),
                "ZAPRET//CTRL",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            PortableCheck.IsChecked = ConfigService.PortableMode;
            MessageBox.Show("Не удалось изменить режим:\n\n" + ex.Message,
                "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void FavoritesOnlyCheck_Click(object sender, RoutedEventArgs e) => _profilesView?.Refresh();

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: StrategyProfile profile })
            return;

        profile.IsFavorite = !profile.IsFavorite;

        if (profile.IsFavorite)
        {
            if (!_config.FavoriteProfiles.Any(name =>
                    name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
                _config.FavoriteProfiles.Add(profile.Name);
        }
        else
        {
            _config.FavoriteProfiles.RemoveAll(name =>
                name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        }

        ConfigService.Save(_config);
        _profilesView?.Refresh();
        AppLog.Info(profile.IsFavorite
            ? $"Стратегия добавлена в избранное: {profile.Name}."
            : $"Стратегия убрана из избранного: {profile.Name}.");
    }

    private void LogSearch_TextChanged(object sender, TextChangedEventArgs e) => _logView?.Refresh();

    private void LogFilter_Click(object sender, RoutedEventArgs e) => _logView?.Refresh();

    private bool FilterLog(object obj)
    {
        if (obj is not LogEntry entry)
            return false;

        var query = LogSearchBox?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(query) &&
            !entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase))
            return false;

        return entry.Level switch
        {
            LogLevel.Info => LogInfoCheck?.IsChecked == true,
            LogLevel.Warn => LogWarnCheck?.IsChecked == true,
            LogLevel.Error => LogErrorCheck?.IsChecked == true,
            LogLevel.Success => LogSuccessCheck?.IsChecked == true,
            _ => true
        };
    }

    private void CopyLogEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LogEntry entry })
            CopyText($"{entry.Time:yyyy-MM-dd HH:mm:ss} [{entry.Level.ToString().ToUpperInvariant()}] {entry.Message}");
    }

    private void CopyVisibleLog_Click(object sender, RoutedEventArgs e)
    {
        var lines = _allLogs
            .Where(FilterLog)
            .Select(entry => $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{entry.Level.ToString().ToUpperInvariant()}] {entry.Message}")
            .ToList();

        if (lines.Count == 0)
        {
            MessageBox.Show("В журнале нет строк по текущему фильтру.", "ZAPRET//CTRL",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CopyText(string.Join(Environment.NewLine, lines));
    }

    private void ExportErrors_Click(object sender, RoutedEventArgs e)
    {
        var errors = _allLogs.Where(entry => entry.Level == LogLevel.Error).ToList();
        if (errors.Count == 0)
        {
            MessageBox.Show("Ошибок в журнале нет.", "ZAPRET//CTRL",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспорт ошибок",
            Filter = "Текстовый файл (*.txt)|*.txt",
            FileName = $"zapret-errors-{DateTime.Now:yyyyMMdd-HHmm}.txt"
        };

        if (dialog.ShowDialog() != true)
            return;

        var builder = new StringBuilder();
        builder.AppendLine("ZAPRET//CTRL — ошибки журнала");
        builder.AppendLine("Дата: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine();

        foreach (var entry in errors)
            builder.AppendLine($"{entry.Time:yyyy-MM-dd HH:mm:ss} {entry.Message}");

        try
        {
            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(false));
            MessageBox.Show("Ошибки сохранены:\n" + dialog.FileName, "ZAPRET//CTRL",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось сохранить файл:\n\n" + ex.Message, "ZAPRET//CTRL",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopHold_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _suppressToggleClick = false;
        if (IsBypassRunning)
            _toggleHoldStart = DateTime.Now;
    }

    private void StopHold_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsBypassRunning)
            return;

        if ((DateTime.Now - _toggleHoldStart).TotalSeconds < 1.2)
        {
            _suppressToggleClick = true;
            LockHint = "Удерживай кнопку 1,2 с для остановки";
        }
    }

    // ===================== HELPERS =====================
    private StrategyProfile? FindProfile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return _profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractProfileName(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var text = title.Trim();
        var marker = text.IndexOf("zapret:", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
            text = text[(marker + "zapret:".Length)..].Trim();

        foreach (var separator in new[] { " [", " — ", " - " })
        {
            var cut = text.IndexOf(separator, StringComparison.Ordinal);
            if (cut > 0)
                text = text[..cut].Trim();
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private void OnLogEntry(LogEntry entry)
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _logs.Insert(0, entry);
            while (_logs.Count > 60)
                _logs.RemoveAt(_logs.Count - 1);

            _allLogs.Insert(0, entry);
            while (_allLogs.Count > 300)
                _allLogs.RemoveAt(_allLogs.Count - 1);
        }));
    }

    private static string FormatUptime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;

        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }

    private static string LineCountText(int count)
    {
        var mod10 = count % 10;
        var mod100 = count % 100;

        var word = mod10 == 1 && mod100 != 11
            ? "строка"
            : mod10 is >= 2 and <= 4 && (mod100 < 12 || mod100 > 14)
                ? "строки"
                : "строк";

        return $"{count} {word}";
    }

    private static string GetExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path) &&
            !System.IO.Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return Process.GetCurrentProcess().MainModule?.FileName ?? "";
    }

    private static void OpenFolder(string path)
    {
        if (Directory.Exists(path))
            OpenPath(path);
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            // Проводник может быть недоступен — не критично.
        }
    }

    private static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            MessageBox.Show("Не удалось скопировать в буфер обмена.",
                "ZAPRET//CTRL", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static Brush MakeBrush(byte r, byte g, byte b)
        => MakeBrush(0xFF, r, g, b);

    private static Brush MakeBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
