using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ZapretControl.Core;

namespace ZapretControl;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsSingleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\ZapretControl.SingleInstance", out var createdNew);
        _ownsSingleInstance = createdNew;

        if (!createdNew)
        {
            // Второй экземпляр не нужен: показываем уже запущенное окно
            // и выходим, чтобы не плодить трей-иконки и менеджеры.
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        AppLog.Info("Запуск ZAPRET//CTRL.");

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("Завершение работы приложения. Обход не останавливается.");

        if (_ownsSingleInstance)
        {
            try
            {
                _singleInstance?.ReleaseMutex();
            }
            catch
            {
                // Мьютекс мог быть уже освобождён при аварийном завершении.
            }
        }

        _singleInstance?.Dispose();
        _singleInstance = null;

        base.OnExit(e);
    }

    private static void ActivateExistingInstance()
    {
        try
        {
            var current = Process.GetCurrentProcess();

            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id == current.Id)
                {
                    process.Dispose();
                    continue;
                }

                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                    handle = FindWindowByProcessId(process.Id);

                if (handle != IntPtr.Zero)
                {
                    ShowWindow(handle, 9); // SW_RESTORE
                    SetForegroundWindow(handle);
                }

                process.Dispose();
            }
        }
        catch
        {
            // Если не удалось поднять старое окно, второй экземпляр просто
            // завершится — это безопаснее, чем два независимых менеджера.
        }
    }

    private static IntPtr FindWindowByProcessId(int processId)
    {
        var result = IntPtr.Zero;
        var buffer = new StringBuilder(256);

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid != processId)
                return true;

            buffer.Clear();
            GetWindowText(hWnd, buffer, buffer.Capacity);

            if (!buffer.ToString().Contains("ZAPRET", StringComparison.OrdinalIgnoreCase))
                return true;

            result = hWnd;
            return false;
        }, IntPtr.Zero);

        return result;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Необработанная ошибка UI: " + e.Exception);
        MessageBox.Show(
            "Произошла ошибка интерфейса:\n\n" + e.Exception.Message +
            "\n\nПриложение продолжит работу. Подробности — в логе.",
            "ZAPRET//CTRL",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            AppLog.Error("Необработанная ошибка: " + exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("Ошибка фоновой задачи: " + e.Exception);
        e.SetObserved();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
