using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Threading;
using SpaceView.Native;

namespace SpaceView;

public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _customIcon;
    private readonly Action _toggleMainWindowAction;
    private readonly Action _rescanAction;
    private readonly Action _exitApplicationAction;

    private TrayHoverWindow? _hoverWindow;
    private readonly DispatcherTimer _hoverWatchTimer;
    private NtfsNative.POINT _hoverAnchorPoint;
    private DateTime _lastHoverRefresh = DateTime.MinValue;
    private bool _disposed;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public TrayIconManager(
        Action toggleMainWindowAction,
        Action rescanAction,
        Action exitApplicationAction)
    {
        _toggleMainWindowAction = toggleMainWindowAction;
        _rescanAction = rescanAction;
        _exitApplicationAction = exitApplicationAction;

        _customIcon = CreateStorageTrayIcon();

        var contextMenu = new ContextMenuStrip();
        var showHideItem = new ToolStripMenuItem("Show / Hide SpaceView");
        showHideItem.Font = new Font(showHideItem.Font, FontStyle.Bold);
        showHideItem.Click += (_, _) => _toggleMainWindowAction();

        var rescanItem = new ToolStripMenuItem("Rescan Current Drive");
        rescanItem.Click += (_, _) => _rescanAction();

        var exitItem = new ToolStripMenuItem("Exit SpaceView");
        exitItem.Click += (_, _) => _exitApplicationAction();

        contextMenu.Items.Add(showHideItem);
        contextMenu.Items.Add(rescanItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _customIcon,
            Visible = true,
            Text = string.Empty, // Empty so native text tooltip does not overlap custom hover window
            ContextMenuStrip = contextMenu
        };

        _notifyIcon.MouseMove += OnNotifyIconMouseMove;
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;

        _hoverWatchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _hoverWatchTimer.Tick += OnHoverWatchTimerTick;
    }

    public Icon AppIcon => _customIcon;

    private void OnNotifyIconMouseMove(object? sender, MouseEventArgs e)
    {
        NtfsNative.GetCursorPos(out _hoverAnchorPoint);

        if (_hoverWindow == null)
        {
            _hoverWindow = new TrayHoverWindow();
        }

        if (!_hoverWindow.IsVisible)
        {
            _hoverWindow.Show();
            _hoverWindow.RefreshAndPositionNearCursor();
            _lastHoverRefresh = DateTime.UtcNow;
            _hoverWatchTimer.Start();
        }
        else if ((DateTime.UtcNow - _lastHoverRefresh).TotalSeconds > 2.5)
        {
            _hoverWindow.RefreshAndPositionNearCursor();
            _lastHoverRefresh = DateTime.UtcNow;
        }
    }

    private void OnHoverWatchTimerTick(object? sender, EventArgs e)
    {
        if (_hoverWindow == null || !_hoverWindow.IsVisible)
        {
            _hoverWatchTimer.Stop();
            return;
        }

        if ( NtfsNative.GetCursorPos(out var pt))
        {
            if (!_hoverWindow.IsPointNearWindowOrAnchor(pt, _hoverAnchorPoint))
            {
                HideHoverPopup();
            }
        }
    }

    public void HideHoverPopup()
    {
        _hoverWatchTimer.Stop();
        if (_hoverWindow is { IsVisible: true })
        {
            _hoverWindow.Hide();
        }
    }

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            HideHoverPopup();
            _toggleMainWindowAction();
        }
    }

    private static Icon CreateStorageTrayIcon()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
            {
                Icon? associated = Icon.ExtractAssociatedIcon(exePath);
                if (associated != null)
                {
                    return associated;
                }
            }
        }
        catch
        {
            // Fallback to programmatic drawing if extraction fails
        }

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Outer dark slate circle
            using var bgBrush = new SolidBrush(Color.FromArgb(255, 15, 23, 42));
            g.FillEllipse(bgBrush, 1, 1, 29, 29);

            // Usage pie segments
            using var cyanBrush = new SolidBrush(Color.FromArgb(255, 56, 189, 248));
            g.FillPie(cyanBrush, 3, 3, 25, 25, -90, 235);

            using var emeraldBrush = new SolidBrush(Color.FromArgb(255, 52, 211, 153));
            g.FillPie(emeraldBrush, 3, 3, 25, 25, 145, 125);

            // Inner cutout for donut gauge look
            using var centerBrush = new SolidBrush(Color.FromArgb(255, 15, 23, 42));
            g.FillEllipse(centerBrush, 10, 10, 11, 11);

            using var borderPen = new Pen(Color.FromArgb(255, 148, 163, 184), 1.5f);
            g.DrawEllipse(borderPen, 1.5f, 1.5f, 28f, 28f);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(hIcon);
            return (Icon)tempIcon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _hoverWatchTimer.Stop();
        if (_hoverWindow != null)
        {
            _hoverWindow.Close();
            _hoverWindow = null;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _customIcon.Dispose();
    }
}
