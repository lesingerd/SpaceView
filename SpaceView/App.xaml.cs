using System.Windows;
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
        base.OnStartup(e);
        PageFileViewModel.StartBackgroundRefresh();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        PageFileViewModel.StopBackgroundRefresh();
        base.OnExit(e);
    }
}
