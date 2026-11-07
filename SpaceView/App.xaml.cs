using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using SpaceView.Core;
using SpaceView.Models;

namespace SpaceView;

public partial class App : System.Windows.Application
{
    /// <summary>Command-line options (drives to scan, instance to wait for); set before the main window is created.</summary>
    public static StartupOptions StartupOptions { get; private set; } = StartupOptions.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupOptions = StartupOptions.Parse(e.Args);

        // Register the safety nets first so a failure while the window is built is logged too
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error($"Unhandled exception (terminating={args.IsTerminating})", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        AppLog.Info($"SpaceView {VersionText()} started; elevated={ElevationHelper.IsElevated}; " +
                    $"args=[{string.Join(' ', e.Args)}]; OS {Environment.OSVersion.Version}");

        base.OnStartup(e);
        PageFileViewModel.StartBackgroundRefresh();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        PageFileViewModel.StopBackgroundRefresh();
        AppLog.Info($"SpaceView exiting (exit code {e.ApplicationExitCode})");
        base.OnExit(e);
    }

    private static string VersionText()
    {
        try
        {
            return Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException or StackOverflowException or AccessViolationException or SEHException;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception", e.Exception);
        if (IsFatal(e.Exception))
            return; // not handled: the process ends, as it would without the handler

        e.Handled = true;
        try
        {
            if (MainWindow is MainWindow window)
                window.StatusTextBlock.Text = $"An unexpected error was handled: {e.Exception.Message} (see spaceview.log)";
        }
        catch
        {
            // The status bar is only a courtesy
        }
    }
}
