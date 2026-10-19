using System.Windows;
using System.Windows.Media;
using SpaceView.Models;
using SpaceView.Native;

namespace SpaceView;

public partial class TrayHoverWindow : Window
{
    public TrayHoverWindow()
    {
        InitializeComponent();
    }

    public void RefreshAndPositionNearCursor()
    {
        DrivesItemsControl.ItemsSource = DriveItemViewModel.GetReadyDrives();
        UpdateLayout();

        NtfsNative.GetCursorPos(out var pt);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(pt.X, pt.Y));
        var workArea = screen.WorkingArea;

        double dpiX = 1.0;
        double dpiY = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            dpiX = source.CompositionTarget.TransformToDevice.M11;
            dpiY = source.CompositionTarget.TransformToDevice.M22;
        }
        else
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            dpiX = dpi.DpiScaleX;
            dpiY = dpi.DpiScaleY;
        }

        double cursorDipX = pt.X / dpiX;
        double cursorDipY = pt.Y / dpiY;

        double workLeft = workArea.Left / dpiX;
        double workTop = workArea.Top / dpiY;
        double workRight = workArea.Right / dpiX;
        double workBottom = workArea.Bottom / dpiY;

        double winWidth = ActualWidth > 0 ? ActualWidth : Width;
        double winHeight = ActualHeight > 0 ? ActualHeight : 220;

        double targetLeft = cursorDipX - (winWidth / 2);
        double targetTop = cursorDipY - winHeight - 12;

        // Clamp within screen working area
        if (targetLeft + winWidth > workRight - 6)
            targetLeft = workRight - winWidth - 6;
        if (targetLeft < workLeft + 6)
            targetLeft = workLeft + 6;

        if (targetTop < workTop + 6)
            targetTop = cursorDipY + 16;
        if (targetTop + winHeight > workBottom - 4)
            targetTop = workBottom - winHeight - 4;

        Left = targetLeft;
        Top = targetTop;
    }

    public bool IsPointNearWindowOrAnchor(NtfsNative.POINT cursorPhysical, NtfsNative.POINT anchorPhysical)
    {
        // Check distance from the tray icon anchor where hover started
        int dxAnchor = Math.Abs(cursorPhysical.X - anchorPhysical.X);
        int dyAnchor = Math.Abs(cursorPhysical.Y - anchorPhysical.Y);
        if (dxAnchor <= 28 && dyAnchor <= 28)
            return true;

        var dpi = VisualTreeHelper.GetDpi(this);
        double dipX = cursorPhysical.X / dpi.DpiScaleX;
        double dipY = cursorPhysical.Y / dpi.DpiScaleY;

        return dipX >= Left - 16 &&
               dipX <= Left + ActualWidth + 16 &&
               dipY >= Top - 16 &&
               dipY <= Top + ActualHeight + 24;
    }
}
